/*
PcapIsoExtractor — pulls the DualSense 4-channel audio/haptics stream out of a
USBPcap capture and writes it as WAV files plus activity statistics.

Usage: DSCompatProbe.exe parsepcap <capture.pcap> <usbDeviceAddress> [outDir]

The iso OUT stream is 3840-byte URBs every 10 ms: 480 frames x 4 channels x
16-bit PCM at 48 kHz. Channels 1/2 are listening audio (pad speaker/jack),
channels 3/4 drive the left/right haptic actuators.
*/

using System.Text;

namespace DSCompatProbe;

internal static class PcapIsoExtractor
{
    private const int FRAME_BYTES = 8; // 4ch x 16-bit

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: DSCompatProbe parsepcap <capture.pcap> <usbDeviceAddress> [outDir]");
            return 2;
        }

        string pcapPath = args[0];
        ushort deviceAddress = ushort.Parse(args[1]);
        string outDir = args.Length > 2 ? args[2] : Path.GetDirectoryName(Path.GetFullPath(pcapPath));

        using var stream = new FileStream(pcapPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1 << 20);
        using var reader = new BinaryReader(stream);

        uint magic = reader.ReadUInt32();
        if (magic != 0xA1B2C3D4 && magic != 0xA1B23C4D)
        {
            Console.Error.WriteLine($"Not a little-endian pcap (magic 0x{magic:X8})");
            return 1;
        }

        stream.Seek(20, SeekOrigin.Current); // rest of global header

        using var audioOut = new MemoryStream();   // ch1/2 interleaved s16
        using var hapticsOut = new MemoryStream(); // ch3/4 interleaved s16
        long urbs = 0, isoBytes = 0;
        long[] sumSquares = new long[4];
        long[] peak = new long[4];
        long frames = 0;

        byte[] packet = new byte[1 << 17];
        while (stream.Position + 16 <= stream.Length)
        {
            stream.Seek(8, SeekOrigin.Current); // ts_sec, ts_usec
            uint inclLen = reader.ReadUInt32();
            stream.Seek(4, SeekOrigin.Current); // orig_len
            if (inclLen > packet.Length || stream.Position + inclLen > stream.Length)
            {
                break;
            }

            int read = stream.Read(packet, 0, (int)inclLen);
            if (read < inclLen)
            {
                break;
            }

            // USBPCAP_BUFFER_PACKET_HEADER
            if (inclLen < 27)
            {
                continue;
            }

            ushort headerLen = BitConverter.ToUInt16(packet, 0);
            ushort device = BitConverter.ToUInt16(packet, 19);
            byte endpoint = packet[21];
            byte transfer = packet[22];
            uint dataLength = BitConverter.ToUInt32(packet, 23);

            if (device != deviceAddress || transfer != 0 /* iso */ ||
                (endpoint & 0x80) != 0 /* IN */ || dataLength == 0)
            {
                continue;
            }

            if (headerLen + dataLength > inclLen)
            {
                continue;
            }

            urbs++;
            isoBytes += dataLength;
            int sampleFrames = (int)(dataLength / FRAME_BYTES);
            for (int f = 0; f < sampleFrames; f++)
            {
                int baseOff = headerLen + f * FRAME_BYTES;
                for (int c = 0; c < 4; c++)
                {
                    short v = BitConverter.ToInt16(packet, baseOff + c * 2);
                    sumSquares[c] += (long)v * v;
                    long av = Math.Abs((long)v);
                    if (av > peak[c])
                    {
                        peak[c] = av;
                    }
                }

                audioOut.Write(packet, baseOff, 4);
                hapticsOut.Write(packet, baseOff + 4, 4);
            }

            frames += sampleFrames;
        }

        Console.WriteLine($"iso OUT URBs: {urbs}, bytes: {isoBytes}, frames: {frames} (~{frames / 48000.0:F1} s of stream)");
        if (frames == 0)
        {
            Console.Error.WriteLine("No matching iso data found.");
            return 1;
        }

        for (int c = 0; c < 4; c++)
        {
            double rms = Math.Sqrt(sumSquares[c] / (double)frames);
            Console.WriteLine($"ch{c + 1}: rms={rms:F0} peak={peak[c]} ({peak[c] / 32768.0:P1} FS)");
        }

        string audioPath = Path.Combine(outDir, "iso_audio_ch12.wav");
        string hapticsPath = Path.Combine(outDir, "iso_haptics_ch34.wav");
        WriteWav(audioPath, audioOut, 48000, 2);
        WriteWav(hapticsPath, hapticsOut, 48000, 2);
        Console.WriteLine($"wrote {audioPath}");
        Console.WriteLine($"wrote {hapticsPath}");
        return 0;
    }

    private static void WriteWav(string path, MemoryStream pcm, int rate, short channels)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        int dataLen = (int)pcm.Length;
        short blockAlign = (short)(channels * 2);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataLen);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write(channels);
        w.Write(rate);
        w.Write(rate * blockAlign);
        w.Write(blockAlign);
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataLen);
        pcm.Position = 0;
        pcm.CopyTo(fs);
    }
}
