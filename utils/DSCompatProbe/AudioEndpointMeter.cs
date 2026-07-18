using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DSCompatProbe;

internal static class AudioEndpointMeter
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: DSCompatProbe meteraudio <endpointId> [seconds=8]");
            return 2;
        }

        int seconds = args.Length > 1 && int.TryParse(args[1], out int parsed)
            ? Math.Clamp(parsed, 1, 60)
            : 8;

        using var enumerator = new MMDeviceEnumerator();
        using MMDevice endpoint = enumerator.GetDevice(args[0]);
        using var capture = new WasapiLoopbackCapture(endpoint);
        WaveFormat format = capture.WaveFormat;
        if (format.BitsPerSample != 32)
        {
            Console.Error.WriteLine($"unsupported loopback format: {format}");
            return 2;
        }

        int channels = format.Channels;
        double[] squares = new double[channels];
        float[] peaks = new float[channels];
        long frameCount = 0;
        object gate = new();

        capture.DataAvailable += (_, e) =>
        {
            ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(
                e.Buffer.AsSpan(0, e.BytesRecorded));
            int frames = samples.Length / channels;
            lock (gate)
            {
                for (int frame = 0; frame < frames; frame++)
                {
                    for (int channel = 0; channel < channels; channel++)
                    {
                        float value = samples[frame * channels + channel];
                        squares[channel] += value * value;
                        peaks[channel] = Math.Max(peaks[channel], Math.Abs(value));
                    }
                }

                frameCount += frames;
            }
        };

        Console.WriteLine($"Metering {endpoint.FriendlyName}");
        Console.WriteLine($"Format: {format.SampleRate} Hz, {channels} channels, {format.BitsPerSample}-bit {format.Encoding}");
        capture.StartRecording();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        capture.StopRecording();

        lock (gate)
        {
            Console.WriteLine($"Captured frames: {frameCount}");
            for (int channel = 0; channel < channels; channel++)
            {
                double rms = frameCount > 0 ? Math.Sqrt(squares[channel] / frameCount) : 0;
                Console.WriteLine($"ch{channel + 1}: rms={rms:F6}, peak={peaks[channel]:F6}");
            }
        }

        return frameCount > 0 ? 0 : 1;
    }
}
