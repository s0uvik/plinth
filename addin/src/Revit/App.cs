// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/plugin-r26/App.cs (one copy per Revit year in rvt-mcp), commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for B2M: one App for every year; the year is read from Revit at runtime; the ribbon,
// toasts, history, settings, reports, baked tools, localization and update checker are not ported.
// Revit 2024 (.NET Framework 4.8) listens on loopback TCP; later years on a named pipe.

using System;
using System.Reflection;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using B2M.Revit.Bridge;
using B2M.Revit.Commands;
using B2M.Revit.Core;
using B2M.Revit.Core.Health;
using B2M.Revit.Core.Logging;
using B2M.Revit.Core.Security;
using B2M.Revit.Core.Transport;

namespace B2M.Revit
{
    public sealed class App : IExternalApplication
    {
        private static readonly TimeSpan ObserveEvery = TimeSpan.FromSeconds(2);

        private RequestEventHandler _handler;
        private ExternalEvent _externalEvent;
        private ITransportServer _transport;
        private HostHealth _health;
        private DateTime _lastObserved = DateTime.MinValue;

        public static string AddinVersion
        {
            get
            {
                var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
                var plus = version.IndexOf('+');
                return plus < 0 ? version : version.Substring(0, plus);
            }
        }

        public Result OnStartup(UIControlledApplication application)
        {
            var year = application.ControlledApplication.VersionNumber;
            AddinLog.Initialize(year);
            AddinLog.Write("App", "Starting B2M.Revit " + AddinVersion + " in Revit " + year);
            try
            {
                var dispatcher = CommandCatalog.Create();
#if NETFRAMEWORK
                const string transportKind = "tcp";
#else
                const string transportKind = "pipe";
#endif
                _health = new HostHealth(AddinVersion, year, transportKind, dispatcher.Describe());
                _handler = new RequestEventHandler(dispatcher, _health);
                _externalEvent = ExternalEvent.Create(_handler);
                _handler.Attach(_externalEvent);

                application.ControlledApplication.FailuresProcessing += FailureScope.OnFailuresProcessing;
                application.Idling += OnIdling;

                var publisher = new DiscoveryPublisher(B2MPaths.Discovery, year, AddinVersion, m => AddinLog.Write("Discovery", m));
#if NETFRAMEWORK
                _transport = new TcpTransportServer(publisher, m => AddinLog.Write("Transport", m));
#else
                _transport = new PipeTransportServer(publisher, m => AddinLog.Write("Transport", m));
#endif
                _transport.Start(_handler);
                AddinLog.Write("App", "Ready");
            }
            catch (Exception ex)
            {
                // Never take Revit down with us: log and stay loaded but unreachable.
                AddinLog.Write("App", "Startup failed: " + ex);
            }
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try { _transport?.Stop(); } catch (Exception ex) { AddinLog.Write("App", "Transport stop failed: " + ex.Message); }
            _handler?.CancelAll();
            application.ControlledApplication.FailuresProcessing -= FailureScope.OnFailuresProcessing;
            application.Idling -= OnIdling;
            _externalEvent?.Dispose();
            AddinLog.Write("App", "Stopped");
            return Result.Succeeded;
        }

        /// <summary>Keeps health's document fields fresh without touching Revit from the transport thread.</summary>
        private void OnIdling(object sender, IdlingEventArgs e)
        {
            var now = DateTime.UtcNow;
            if (now - _lastObserved < ObserveEvery) return;
            _lastObserved = now;
            try
            {
                var doc = (sender as UIApplication)?.ActiveUIDocument?.Document;
                _health?.ObserveDocument(doc != null, doc?.Title);
            }
            catch
            {
                // Observation is best effort.
            }
        }
    }
}
