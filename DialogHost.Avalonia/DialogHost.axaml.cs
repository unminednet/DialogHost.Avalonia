﻿using System;
using System.Collections.Generic;
using System.Reactive;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Avalonia.DialogHost {
    public class DialogHost : ContentControl {
        public const string ContentCoverGridName = "PART_ContentCoverGrid";
        public const string OverlayLayerName = "PART_OverlayLayer";
        
        private static readonly HashSet<DialogHost> LoadedInstances = new();

        public static readonly DirectProperty<DialogHost, string?> IdentifierProperty =
            AvaloniaProperty.RegisterDirect<DialogHost, string?>(
                nameof(Identifier),
                o => o.Identifier,
                (o, v) => o.Identifier = v);

        public static readonly StyledProperty<object> DialogContentProperty = 
            AvaloniaProperty.Register<DialogHost, object>(nameof(DialogContent));

        public static readonly StyledProperty<IDataTemplate> DialogContentTemplateProperty = 
            AvaloniaProperty.Register<DialogHost, IDataTemplate>(nameof(DialogContentTemplate));

        public static readonly StyledProperty<IBrush> OverlayBackgroundProperty = 
            AvaloniaProperty.Register<DialogHost, IBrush>(nameof(OverlayBackground));

        public static readonly StyledProperty<Thickness> DialogMarginProperty = 
            AvaloniaProperty.Register<DialogHost, Thickness>(nameof(DialogMargin));

        public static readonly DirectProperty<DialogHost, bool> IsOpenProperty =
            AvaloniaProperty.RegisterDirect<DialogHost, bool>(
                nameof(IsOpen),
                o => o.IsOpen,
                (o, v) => {
                    o.IsOpen = v;
                    IsOpenPropertyChangedCallback(o, v);
                });

        public static readonly RoutedEvent DialogOpenedEvent =
            RoutedEvent.Register<DialogHost, DialogOpenedEventArgs>(nameof(DialogOpened), RoutingStrategies.Bubble);

        public static readonly DirectProperty<DialogHost, bool> CloseOnClickAwayProperty =
            AvaloniaProperty.RegisterDirect<DialogHost, bool>(
                nameof(CloseOnClickAway),
                o => o.CloseOnClickAway,
                (o, v) => o.CloseOnClickAway = v);

        public static readonly DirectProperty<DialogHost, object?> CloseOnClickAwayParameterProperty =
            AvaloniaProperty.RegisterDirect<DialogHost, object?>(
                nameof(CloseOnClickAwayParameter),
                o => o.CloseOnClickAwayParameter,
                (o, v) => o.CloseOnClickAwayParameter = v);

        public static readonly RoutedEvent DialogClosingEvent =
            RoutedEvent.Register<DialogHost, DialogClosingEventArgs>(nameof(DialogClosing), RoutingStrategies.Bubble);

        public static readonly DirectProperty<DialogHost, DialogClosingEventHandler> DialogClosingCallbackProperty =
            AvaloniaProperty.RegisterDirect<DialogHost, DialogClosingEventHandler>(
                nameof(DialogClosingCallback),
                o => o.DialogClosingCallback,
                (o, v) => o.DialogClosingCallback = v);

        public static readonly DirectProperty<DialogHost, DialogOpenedEventHandler> DialogOpenedCallbackProperty =
            AvaloniaProperty.RegisterDirect<DialogHost, DialogOpenedEventHandler>(
                nameof(DialogOpenedCallback),
                o => o.DialogOpenedCallback,
                (o, v) => o.DialogOpenedCallback = v);

        private DialogOpenedEventHandler _dialogOpenedCallback;

        public DialogOpenedEventHandler DialogOpenedCallback {
            get { return _dialogOpenedCallback; }
            set { SetAndRaise(DialogOpenedCallbackProperty, ref _dialogOpenedCallback, value); }
        }

        private DialogClosingEventHandler? _asyncShowClosingEventHandler;
        private DialogOpenedEventHandler? _asyncShowOpenedEventHandler;

        private bool _closeOnClickAway;

        private object? _closeOnClickAwayParameter;

        private DialogClosingEventHandler _dialogClosingCallback;

        private TaskCompletionSource<object?>? _dialogTaskCompletionSource;

        private string? _identifier = default;

        private bool _isOpen;
        private IInputElement? _restoreFocusDialogClose;
        
        // TODO: Implement commands
        // public ICommand OpenDialogCommand { get; }
        // public ICommand CloseDialogCommand { get; }

        public string? Identifier {
            get { return _identifier; }
            set { SetAndRaise(IdentifierProperty, ref _identifier, value); }
        }

        public object DialogContent {
            get { return GetValue(DialogContentProperty); }
            set { SetValue(DialogContentProperty, value); }
        }

        public IDataTemplate DialogContentTemplate {
            get { return GetValue(DialogContentTemplateProperty); }
            set { SetValue(DialogContentTemplateProperty, value); }
        }

        public IBrush OverlayBackground {
            get { return GetValue(OverlayBackgroundProperty); }
            set { SetValue(OverlayBackgroundProperty, value); }
        }

        public Thickness DialogMargin {
            get { return GetValue(DialogMarginProperty); }
            set { SetValue(DialogMarginProperty, value); }
        }

        public bool IsOpen {
            get { return _isOpen; }
            set { SetAndRaise(IsOpenProperty, ref _isOpen, value); }
        }

        public bool CloseOnClickAway {
            get { return _closeOnClickAway; }
            set { SetAndRaise(CloseOnClickAwayProperty, ref _closeOnClickAway, value); }
        }

        public object? CloseOnClickAwayParameter {
            get { return _closeOnClickAwayParameter; }
            set { SetAndRaise(CloseOnClickAwayParameterProperty, ref _closeOnClickAwayParameter, value); }
        }

        /// <summary>
        /// Returns a DialogSession for the currently open dialog for managing it programmatically. If no dialog is open, CurrentSession will return null
        /// </summary>
        public DialogSession? CurrentSession { get; private set; }

        public DialogClosingEventHandler DialogClosingCallback {
            get { return _dialogClosingCallback; }
            set { SetAndRaise(DialogClosingCallbackProperty, ref _dialogClosingCallback, value); }
        }

        private OverlayLayer? _overlayLayer;
        private OverlayPopupHost? _overlayPopupHost;
        private static void IsOpenPropertyChangedCallback(DialogHost dialogHost, bool newValue) {
            if (!newValue) {
                object? closeParameter = null;
                if (dialogHost.CurrentSession is { } session)
                {
                    if (!session.IsEnded)
                    {
                        session.Close(session.CloseParameter);
                    }
                    //DialogSession.Close may attempt to cancel the closing of the dialog.
                    //When the dialog is closed in this manner it is not valid
                    if (!session.IsEnded)
                    {
                        throw new InvalidOperationException($"Cannot cancel dialog closing after {nameof(IsOpen)} property has been set to {bool.FalseString}");
                    }
                    closeParameter = session.CloseParameter;
                    dialogHost.CurrentSession = null;
                }
                
                dialogHost._overlayPopupHost?.Hide();
                
                //NB: _dialogTaskCompletionSource is only set in the case where the dialog is shown with Show
                dialogHost._dialogTaskCompletionSource?.TrySetResult(closeParameter);
                
                dialogHost._restoreFocusDialogClose?.Focus();

                return;
            }
            
            dialogHost.CurrentSession = new DialogSession(dialogHost);
            dialogHost._restoreFocusDialogClose = FocusManager.Instance.Current;

            //multiple ways of calling back that the dialog has opened:
            // * routed event
            // * straight forward dependency property 
            // * handler provided to the async show method
            var dialogOpenedEventArgs = new DialogOpenedEventArgs(dialogHost.CurrentSession, DialogOpenedEvent);
            dialogHost.OnDialogOpened(dialogOpenedEventArgs);
            dialogHost.DialogOpenedCallback?.Invoke(dialogHost, dialogOpenedEventArgs);
            dialogHost._asyncShowOpenedEventHandler?.Invoke(dialogHost, dialogOpenedEventArgs);

            dialogHost._overlayPopupHost?.Show();
        }

        private IDisposable? _contentCoverGrid_OnPointerReleasedHandler;

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e) {
            _contentCoverGrid_OnPointerReleasedHandler?.Dispose();
            _contentCoverGrid_OnPointerReleasedHandler = this.FindControl<Grid>(ContentCoverGridName)?.AddDisposableHandler(PointerReleasedEvent, ContentCoverGrid_OnPointerReleased);

            _overlayLayer = this.FindControl<OverlayLayer>(OverlayLayerName);
            _overlayPopupHost = new OverlayPopupHost(_overlayLayer) {
                Content = DialogContent, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center
            };
            if (IsOpen) {
                _overlayPopupHost.Show();
            }

            base.OnApplyTemplate(e);
        }

        private void ContentCoverGrid_OnPointerReleased(object sender, PointerReleasedEventArgs e) {
            if (CloseOnClickAway && CurrentSession != null) {
                InternalClose(CloseOnClickAwayParameter);
            }
        }

        protected void OnDialogOpened(DialogOpenedEventArgs dialogOpenedEventArgs) => RaiseEvent(dialogOpenedEventArgs);

        /// <summary>
        /// Raised when a dialog is opened.
        /// </summary>
        public event DialogOpenedEventHandler DialogOpened
        {
            add { AddHandler(DialogOpenedEvent, value); }
            remove { RemoveHandler(DialogOpenedEvent, value); }
        }

        /// <summary>
        /// Raised just before a dialog is closed.
        /// </summary>
        public event EventHandler<DialogClosingEventArgs> DialogClosing
        {
            add { AddHandler(DialogClosingEvent, value); }
            remove { RemoveHandler(DialogClosingEvent, value); }
        }

        protected void OnDialogClosing(DialogClosingEventArgs eventArgs) => RaiseEvent(eventArgs);

        internal void InternalClose(object? parameter) {
            var currentSession = CurrentSession ?? throw new InvalidOperationException($"{nameof(DialogHost)} does not have a current session");

            currentSession.CloseParameter = parameter;
            currentSession.IsEnded = true;

            //multiple ways of calling back that the dialog is closing:
            // * routed event
            // * straight forward IsOpen dependency property 
            // * handler provided to the async show method
            var dialogClosingEventArgs = new DialogClosingEventArgs(currentSession, DialogClosingEvent);
            OnDialogClosing(dialogClosingEventArgs);
            DialogClosingCallback?.Invoke(this, dialogClosingEventArgs);
            _asyncShowClosingEventHandler?.Invoke(this, dialogClosingEventArgs);

            if (dialogClosingEventArgs.IsCancelled) {
                currentSession.IsEnded = false;
                return;
            }

            IsOpen = false;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
            base.OnAttachedToVisualTree(e);
            LoadedInstances.Add(this);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
            base.OnDetachedFromVisualTree(e);
            LoadedInstances.Remove(this);
        }
    }
}