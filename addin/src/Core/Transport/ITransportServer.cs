// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Transport/ITransportServer.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: requests go to an IRequestSink instead of a raw callback; unused status members removed.

using System;

namespace B2M.Revit.Core.Transport
{
    public interface ITransportServer : IDisposable
    {
        /// <summary>"pipe" or "tcp".</summary>
        string Kind { get; }

        bool IsRunning { get; }

        void Start(IRequestSink sink);

        void Stop();
    }
}
