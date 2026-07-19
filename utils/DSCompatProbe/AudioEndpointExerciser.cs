using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DSCompatProbe;

/// <summary>
/// Drives specific audio endpoints for live validation of the virtual
/// DualSense: <c>playtone</c> renders a sine to a chosen render endpoint
/// (never the system default), and <c>recordmic</c> captures a chosen
/// microphone endpoint to a WAV with an RMS/peak summary. Endpoints are
/// selected by MMDevice id or by a case-insensitive friendly-name substring.
/// </summary>
internal static class AudioEndpointExerciser
{
    public static int RunPlayTone(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine(
                "usage: DSCompatProbe playtone <endpointIdOrNameSubstring> [seconds=6] [freqHz=440] [amplitude=0.35]");
            return 2;
        }

        int seconds = ParseIntArg(args, 1, 6, 1, 120);
        double frequency = ParseDoubleArg(args, 2, 440.0, 30.0, 12000.0);
        double amplitude = ParseDoubleArg(args, 3, 0.35, 0.01, 1.0);

        using MMDevice endpoint = FindEndpoint(args[0], DataFlow.Render);
        if (endpoint == null)
        {
            Console.Error.WriteLine($"No active render endpoint matches '{args[0]}'.");
            return 2;
        }

        Console.WriteLine($"Rendering {frequency:0.#} Hz for {seconds} s to: {endpoint.FriendlyName}");
        Console.WriteLine($"Endpoint id: {endpoint.ID}");
        var tone = new SignalGenerator(48000, 2)
        {
            Gain = amplitude,
            Frequency = frequency,
            Type = SignalGeneratorType.Sin,
        };
        using var output = new WasapiOut(endpoint, AudioClientShareMode.Shared,
            useEventSync: true, latency: 50);
        output.Init(tone.Take(TimeSpan.FromSeconds(seconds)));
        output.Play();
        while (output.PlaybackState == PlaybackState.Playing)
        {
            Thread.Sleep(100);
        }
        Console.WriteLine("Tone finished.");
        return 0;
    }

    public static int RunRecordMicrophone(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine(
                "usage: DSCompatProbe recordmic <endpointIdOrNameSubstring> [seconds=8] [out.wav]");
            return 2;
        }

        int seconds = ParseIntArg(args, 1, 8, 1, 120);
        string wavPath = args.Length > 2 ? args[2] : null;

        using MMDevice endpoint = FindEndpoint(args[0], DataFlow.Capture);
        if (endpoint == null)
        {
            Console.Error.WriteLine($"No active capture endpoint matches '{args[0]}'.");
            return 2;
        }

        using var capture = new WasapiCapture(endpoint);
        WaveFormat format = capture.WaveFormat;
        Console.WriteLine($"Recording {seconds} s from: {endpoint.FriendlyName}");
        Console.WriteLine($"Endpoint id: {endpoint.ID}");
        Console.WriteLine($"Format: {format.SampleRate} Hz, {format.Channels} ch, " +
            $"{format.BitsPerSample}-bit {format.Encoding}");

        WaveFileWriter writer = null;
        if (wavPath != null)
        {
            writer = new WaveFileWriter(Path.GetFullPath(wavPath), format);
        }

        double sumSquares = 0;
        double peak = 0;
        long sampleCount = 0;
        object gate = new();
        capture.DataAvailable += (_, e) =>
        {
            lock (gate)
            {
                writer?.Write(e.Buffer, 0, e.BytesRecorded);
                if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
                {
                    for (int offset = 0; offset + 4 <= e.BytesRecorded; offset += 4)
                    {
                        float value = BitConverter.ToSingle(e.Buffer, offset);
                        sumSquares += value * (double)value;
                        peak = Math.Max(peak, Math.Abs(value));
                        sampleCount++;
                    }
                }
                else if (format.BitsPerSample == 16)
                {
                    for (int offset = 0; offset + 2 <= e.BytesRecorded; offset += 2)
                    {
                        double value = BitConverter.ToInt16(e.Buffer, offset) / 32768.0;
                        sumSquares += value * value;
                        peak = Math.Max(peak, Math.Abs(value));
                        sampleCount++;
                    }
                }
            }
        };

        capture.StartRecording();
        for (int elapsed = 0; elapsed < seconds; elapsed++)
        {
            Thread.Sleep(1000);
            lock (gate)
            {
                double runningRms = sampleCount > 0 ? Math.Sqrt(sumSquares / sampleCount) : 0;
                Console.WriteLine($"  t+{elapsed + 1}s samples={sampleCount} " +
                    $"rms={runningRms:F6} peak={peak:F6}");
            }
        }
        capture.StopRecording();
        Thread.Sleep(200);

        lock (gate)
        {
            writer?.Dispose();
            double rms = sampleCount > 0 ? Math.Sqrt(sumSquares / sampleCount) : 0;
            Console.WriteLine($"Captured samples: {sampleCount}");
            Console.WriteLine($"rms={rms:F6} peak={peak:F6}");
            if (wavPath != null)
            {
                Console.WriteLine($"Saved: {Path.GetFullPath(wavPath)}");
            }
            return sampleCount > 0 ? 0 : 1;
        }
    }

    private static MMDevice FindEndpoint(string idOrName, DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            MMDevice byId = enumerator.GetDevice(idOrName);
            if (byId != null && byId.DataFlow == flow && byId.State == DeviceState.Active)
            {
                return byId;
            }
            byId?.Dispose();
        }
        catch (Exception)
        {
            // Not an endpoint id; fall through to name matching.
        }

        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            if (device.FriendlyName.Contains(idOrName, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
            device.Dispose();
        }
        return null;
    }

    private static int ParseIntArg(string[] args, int index, int fallback, int min, int max)
    {
        return args.Length > index && int.TryParse(args[index], out int parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;
    }

    private static double ParseDoubleArg(string[] args, int index, double fallback,
        double min, double max)
    {
        return args.Length > index && double.TryParse(args[index], out double parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;
    }
}
