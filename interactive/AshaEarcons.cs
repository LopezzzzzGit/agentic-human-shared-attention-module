using System.Media;
using System.IO;
using NAudio.Wave;

namespace AshaLive;

/// <summary>
/// Very short, unobtrusive glass earcons for explicit conversation boundaries.
/// They are synthesized locally so ASHA has no asset file or network dependency.
/// </summary>
public static class AshaEarcons
{
    private const int SampleRate = 48_000;

    public static void ConversationStarted() => Play(
        new Tone(720, 1_040, 0.050, 0.115),
        new Tone(1_010, 1_260, 0.068, 0.085));

    public static void ConversationEnded() => Play(
        new Tone(840, 640, 0.058, 0.105),
        new Tone(585, 410, 0.078, 0.080));

    private static void Play(params Tone[] tones)
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream(BuildWave(tones), writable: false);
                using var player = new SoundPlayer(stream);
                player.Load();
                player.PlaySync();
            }
            catch
            {
                // Audio feedback is a refinement, never a reason for voice
                // capture or conversation control to fail.
            }
        });
    }

    private static byte[] BuildWave(IReadOnlyList<Tone> tones)
    {
        using var stream = new MemoryStream();
        using var writer = new WaveFileWriter(stream, new WaveFormat(SampleRate, 16, 1));
        foreach (var tone in tones)
        {
            var frames = Math.Max(1, (int)(tone.Seconds * SampleRate));
            var phase = 0.0;
            for (var index = 0; index < frames; index++)
            {
                var progress = index / (double)frames;
                var frequency = tone.FromHz + (tone.ToHz - tone.FromHz) * progress;
                phase += Math.PI * 2 * frequency / SampleRate;
                var attack = Math.Min(1, progress / 0.12);
                var release = Math.Min(1, (1 - progress) / 0.32);
                var envelope = attack * release;
                var shimmer = Math.Sin(phase) + Math.Sin(phase * 2.02) * 0.16;
                var sample = (short)Math.Clamp(shimmer * envelope * tone.Volume * short.MaxValue, short.MinValue, short.MaxValue);
                writer.WriteByte((byte)(sample & 0xff));
                writer.WriteByte((byte)((sample >> 8) & 0xff));
            }

            // A tiny breath between the two components keeps it click-like,
            // rather than reading as a notification melody.
            for (var silence = 0; silence < SampleRate / 120; silence++)
            {
                writer.WriteByte(0);
                writer.WriteByte(0);
            }
        }
        return stream.ToArray();
    }

    private sealed record Tone(double FromHz, double ToHz, double Seconds, double Volume);
}
