using SharpGen.Runtime;

namespace AmbientMonitor;

internal sealed class HdrCompatibility
{
    internal bool Rejected { get; private set; }
    internal T Select<T>(bool enabled, Func<T> preferred, Func<T> fallback, Action<Exception> rejected)
    {
        if (enabled && !Rejected)
        {
            try { return preferred(); }
            catch (Exception ex) when (IsUnsupported(ex))
            {
                Rejected = true; rejected(ex);
            }
        }
        return fallback();
    }
    internal static bool IsUnsupported(Exception exception)
    {
        int code = exception is SharpGenException graphics ? graphics.ResultCode.Code : exception.HResult;
        return exception is NotSupportedException || unchecked((uint)code) is 0x887A0004 or 0x80004002 or 0x80070057 or 0x80004001;
    }
}
