using WPods.Core;

namespace WPods.Audio;

internal static class EarPlaybackPolicy
{
    internal const int DefaultSeconds = 15;
    internal static int Window(int? seconds) => seconds is 0 or 15 or 60 ? seconds.Value : DefaultSeconds;

    internal static int Count(EarStatus? first, EarStatus? second, bool headphones)
    {
        static bool Known(EarStatus? value) => value is EarStatus.InEar or EarStatus.OutOfEar or EarStatus.InCase or EarStatus.Disconnected;
        int worn = (first == EarStatus.InEar ? 1 : 0) + (second == EarStatus.InEar ? 1 : 0);
        if (headphones) return worn > 0 ? 1 : Known(first) || Known(second) ? 0 : -1;
        return Known(first) && Known(second) ? worn : -1;
    }
}
