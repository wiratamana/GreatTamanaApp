#if !CO_WORKER_USE
namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        private const int DEFAULT_TIMEOUT_MS = 60_000;
    }
}
#endif
