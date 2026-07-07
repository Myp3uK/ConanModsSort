using System.IO;
using System.Media;
using System.Text;

namespace ConanModsSort;

public static class SoftChime
{
    private static byte[]? _wav;

    public static void Play()
    {
        try
        {
            _wav ??= Build();
            var player = new SoundPlayer(new MemoryStream(_wav));
            player.Play();
        }
        catch { }
    }

    private static byte[] Build()
    {
        const int sr = 44100;
        const double dur = 0.35;
        int n = (int)(sr * dur);
        var samples = new short[n];

        const double f1 = 660.0, f2 = 990.0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / sr;
            double env = System.Math.Exp(-t * 6.0);
            double attack = System.Math.Min(1.0, t / 0.008);
            double wave = System.Math.Sin(2 * System.Math.PI * f1 * t) * 0.6
                        + System.Math.Sin(2 * System.Math.PI * f2 * t) * 0.4;
            double v = wave * env * attack * 0.25;
            samples[i] = (short)(v * short.MaxValue);
        }

        return WriteWav(samples, sr);
    }

    private static byte[] WriteWav(short[] samples, int sampleRate)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = samples.Length * 2;

        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataLen);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(sampleRate);
        w.Write(sampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
