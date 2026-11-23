// Test at the registration boundary without taking real global shortcuts from Windows.
namespace WPods.Ui
{
    internal sealed class HostWindow
    {
        public Dictionary<int, WPods.Core.Shortcut> Registered { get; } = [];
        public bool Register(int id, uint mods, uint vk) => Registered.TryAdd(id, new((int)vk, (int)mods));
        public void Unregister(int id) => Registered.Remove(id);
    }

    internal static class NativeUi
    {
        internal const uint ModAlt = 1, ModShift = 4, ModWin = 8;
    }
}

namespace WPods.Core
{
    internal readonly record struct Shortcut(int Vk, int Mods);
    internal sealed record HotkeyConfig(IReadOnlyDictionary<string, Shortcut>? Bindings, bool Sounds);
    internal static class Log { internal static void Info(string area, string message) { } }
}
