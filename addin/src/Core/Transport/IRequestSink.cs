using B2M.Revit.Core.Execution;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Core.Transport
{
    /// <summary>Where a transport hands authenticated requests. Implemented by the Revit-side event handler.</summary>
    public interface IRequestSink
    {
        /// <summary>Queue a request for Revit's main thread. Called on a transport thread.</summary>
        void Submit(PendingRequest request);

        /// <summary>Answer <c>health</c> without touching the Revit API. Called on a transport thread.</summary>
        JObject Health();
    }
}
