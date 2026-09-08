using System.Globalization;
using System.Text;

namespace FreePolarAlign.Imaging.Fits;

/// <summary>
/// Hand-written reader/writer for the subset of FITS actually needed by this
/// project: a single-HDU 2D image (SIMPLE/BITPIX/NAXIS1/NAXIS2/BZERO/BSCALE)
/// plus arbitrary additional header cards (in particular a TAN WCS -- see
/// <see cref="Wcs.TanWcsSolution"/>). Not a general-purpose FITS library:
/// no multi-extension files, no tables, no compression.
/// </summary>
public static class FitsFile
{
    private const int BlockSize = 2880;
    private const int CardSize = 80;

    public static void Write(string path, FitsImage image)
    {
        using FileStream stream = File.Create(path);
        Write(stream, image);
    }

    public static void Write(Stream stream, FitsImage image)
    {
        var cards = new List<FitsCard>
        {
            new("SIMPLE", true, "conforms to FITS standard"),
            new("BITPIX", (long)image.BitPix, "bits per pixel"),
            new("NAXIS", 2L, "number of axes"),
            new("NAXIS1", (long)image.Width, "axis 1 length"),
            new("NAXIS2", (long)image.Height, "axis 2 length"),
            new("BZERO", image.Bzero, "physical = BZERO + BSCALE * array_value"),
            new("BSCALE", image.Bscale, "physical = BZERO + BSCALE * array_value"),
        };
        cards.AddRange(image.ExtraHeader.Cards);

        WriteHeaderBlock(stream, cards);
        WriteDataBlock(stream, image);
    }

    public static FitsImage Read(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Read(stream);
    }

    public static FitsImage Read(Stream stream)
    {
        List<FitsCard> cards = ReadHeaderBlock(stream);

        FitsHeader structural = new();
        FitsHeader extra = new();
        foreach (FitsCard card in cards)
        {
            structural.Set(card.Keyword, card.Value ?? "", card.Comment);
            if (!IsStructuralKeyword(card.Keyword))
            {
                extra.Set(card.Keyword, card.Value ?? "", card.Comment);
            }
        }

        int width = (int)structural.GetDouble("NAXIS1");
        int height = (int)structural.GetDouble("NAXIS2");
        var bitPix = (FitsBitPix)(int)structural.GetDouble("BITPIX");
        double bzero = structural.GetDouble("BZERO", 0.0);
        double bscale = structural.GetDouble("BSCALE", 1.0);

        double[,] pixels = ReadDataBlock(stream, width, height, bitPix, bzero, bscale);

        return new FitsImage(width, height, bitPix, bzero, bscale, pixels, extra);
    }

    private static bool IsStructuralKeyword(string keyword) => keyword is "SIMPLE" or "BITPIX" or "NAXIS" or "NAXIS1" or "NAXIS2" or "BZERO" or "BSCALE" or "END";

    // ---------------- Header ----------------

    private static void WriteHeaderBlock(Stream stream, List<FitsCard> cards)
    {
        var bytes = new List<byte>();
        foreach (FitsCard card in cards)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(FormatCard(card)));
        }

        bytes.AddRange(Encoding.ASCII.GetBytes("END".PadRight(CardSize)));

        int remainder = bytes.Count % BlockSize;
        if (remainder != 0)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(new string(' ', BlockSize - remainder)));
        }

        stream.Write(bytes.ToArray(), 0, bytes.Count);
    }

    private static string FormatCard(FitsCard card)
    {
        string keywordField = card.Keyword.Length > 8 ? card.Keyword[..8] : card.Keyword.PadRight(8);
        string valueField = FormatValue(card.Value);

        string line = keywordField + "= " + valueField;
        if (!string.IsNullOrEmpty(card.Comment))
        {
            line += " / " + card.Comment;
        }

        if (line.Length > CardSize)
        {
            line = line[..CardSize];
        }
        else
        {
            line = line.PadRight(CardSize);
        }

        return line;
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "",
        bool b => (b ? "T" : "F").PadLeft(20),
        long l => l.ToString(CultureInfo.InvariantCulture).PadLeft(20),
        int i => i.ToString(CultureInfo.InvariantCulture).PadLeft(20),
        double d => FormatDouble(d).PadLeft(20),
        string s => "'" + s.Replace("'", "''") + "'",
        _ => throw new NotSupportedException($"Unsupported FITS card value type '{value.GetType()}'.")
    };

    private static string FormatDouble(double d)
    {
        // Round-trippable, and always contains a decimal point/exponent so a
        // reader can distinguish it from an integer value.
        string s = d.ToString("G17", CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('E') && !s.Contains('e'))
        {
            s += ".0";
        }

        return s;
    }

    private static List<FitsCard> ReadHeaderBlock(Stream stream)
    {
        var cards = new List<FitsCard>();
        var blockBuffer = new byte[BlockSize];

        while (true)
        {
            int read = ReadFully(stream, blockBuffer, BlockSize);
            if (read < BlockSize)
            {
                throw new InvalidDataException("Unexpected end of stream while reading FITS header.");
            }

            string block = Encoding.ASCII.GetString(blockBuffer);
            for (int i = 0; i < BlockSize; i += CardSize)
            {
                string cardText = block.Substring(i, CardSize);
                string keyword = cardText[..8].Trim();
                if (keyword == "END")
                {
                    return cards;
                }

                if (keyword.Length == 0)
                {
                    continue;
                }

                if (cardText.Length >= 10 && cardText[8] == '=' && cardText[9] == ' ')
                {
                    cards.Add(ParseValueCard(keyword, cardText[10..]));
                }
                // Commentary cards (COMMENT/HISTORY/blank) are skipped: not part of the Phase 0 subset.
            }
        }
    }

    private static FitsCard ParseValueCard(string keyword, string rest)
    {
        rest = rest.TrimStart();
        object? value;
        string? comment = null;

        if (rest.StartsWith('\''))
        {
            var sb = new StringBuilder();
            int i = 1;
            while (i < rest.Length)
            {
                if (rest[i] == '\'')
                {
                    if (i + 1 < rest.Length && rest[i + 1] == '\'')
                    {
                        sb.Append('\'');
                        i += 2;
                        continue;
                    }

                    i++;
                    break;
                }

                sb.Append(rest[i]);
                i++;
            }

            value = sb.ToString().TrimEnd();
            int slash = rest.IndexOf('/', i);
            if (slash >= 0)
            {
                comment = rest[(slash + 1)..].Trim();
            }
        }
        else
        {
            int slash = rest.IndexOf('/');
            string valuePart = (slash >= 0 ? rest[..slash] : rest).Trim();
            if (slash >= 0)
            {
                comment = rest[(slash + 1)..].Trim();
            }

            if (valuePart == "T")
            {
                value = true;
            }
            else if (valuePart == "F")
            {
                value = false;
            }
            else if (long.TryParse(valuePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
            {
                value = l;
            }
            else if (double.TryParse(valuePart, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            {
                value = d;
            }
            else
            {
                value = valuePart;
            }
        }

        return new FitsCard(keyword, value, comment);
    }

    // ---------------- Data ----------------

    private static void WriteDataBlock(Stream stream, FitsImage image)
    {
        int bytesPerPixel = Math.Abs((int)image.BitPix) / 8;
        long totalBytes = (long)image.Width * image.Height * bytesPerPixel;
        var buffer = new byte[totalBytes];
        int offset = 0;

        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                double physical = image.Pixels[y, x];
                double raw = (physical - image.Bzero) / image.Bscale;
                WriteRawPixel(buffer, ref offset, image.BitPix, raw);
            }
        }

        stream.Write(buffer, 0, buffer.Length);

        int remainder = (int)(totalBytes % BlockSize);
        if (remainder != 0)
        {
            stream.Write(new byte[BlockSize - remainder]);
        }
    }

    private static void WriteRawPixel(byte[] buffer, ref int offset, FitsBitPix bitPix, double raw)
    {
        switch (bitPix)
        {
            case FitsBitPix.Byte:
                buffer[offset++] = (byte)Math.Round(raw, MidpointRounding.AwayFromZero);
                break;
            case FitsBitPix.Int16:
                WriteBigEndian(buffer, ref offset, (short)Math.Round(raw, MidpointRounding.AwayFromZero));
                break;
            case FitsBitPix.Int32:
                WriteBigEndian(buffer, ref offset, (int)Math.Round(raw, MidpointRounding.AwayFromZero));
                break;
            case FitsBitPix.Int64:
                WriteBigEndian(buffer, ref offset, (long)Math.Round(raw, MidpointRounding.AwayFromZero));
                break;
            case FitsBitPix.Float32:
                WriteBigEndian(buffer, ref offset, (float)raw);
                break;
            case FitsBitPix.Float64:
                WriteBigEndian(buffer, ref offset, raw);
                break;
            default:
                throw new NotSupportedException($"Unsupported BITPIX {(int)bitPix}.");
        }
    }

    private static double[,] ReadDataBlock(Stream stream, int width, int height, FitsBitPix bitPix, double bzero, double bscale)
    {
        int bytesPerPixel = Math.Abs((int)bitPix) / 8;
        long totalBytes = (long)width * height * bytesPerPixel;
        var buffer = new byte[totalBytes];
        int read = ReadFully(stream, buffer, buffer.Length);
        if (read < buffer.Length)
        {
            throw new InvalidDataException("Unexpected end of stream while reading FITS data.");
        }

        var pixels = new double[height, width];
        int offset = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double raw = ReadRawPixel(buffer, ref offset, bitPix);
                pixels[y, x] = bzero + bscale * raw;
            }
        }

        return pixels;
    }

    private static double ReadRawPixel(byte[] buffer, ref int offset, FitsBitPix bitPix)
    {
        switch (bitPix)
        {
            case FitsBitPix.Byte:
                return buffer[offset++];
            case FitsBitPix.Int16:
                return ReadBigEndianInt16(buffer, ref offset);
            case FitsBitPix.Int32:
                return ReadBigEndianInt32(buffer, ref offset);
            case FitsBitPix.Int64:
                return ReadBigEndianInt64(buffer, ref offset);
            case FitsBitPix.Float32:
                return ReadBigEndianFloat32(buffer, ref offset);
            case FitsBitPix.Float64:
                return ReadBigEndianFloat64(buffer, ref offset);
            default:
                throw new NotSupportedException($"Unsupported BITPIX {(int)bitPix}.");
        }
    }

    private static void WriteBigEndian(byte[] buffer, ref int offset, short value)
    {
        Span<byte> span = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(span, value);
        span.CopyTo(buffer.AsSpan(offset));
        offset += 2;
    }

    private static void WriteBigEndian(byte[] buffer, ref int offset, int value)
    {
        Span<byte> span = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(span, value);
        span.CopyTo(buffer.AsSpan(offset));
        offset += 4;
    }

    private static void WriteBigEndian(byte[] buffer, ref int offset, long value)
    {
        Span<byte> span = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(span, value);
        span.CopyTo(buffer.AsSpan(offset));
        offset += 8;
    }

    private static void WriteBigEndian(byte[] buffer, ref int offset, float value)
    {
        Span<byte> span = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(span, value);
        span.CopyTo(buffer.AsSpan(offset));
        offset += 4;
    }

    private static void WriteBigEndian(byte[] buffer, ref int offset, double value)
    {
        Span<byte> span = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(span, value);
        span.CopyTo(buffer.AsSpan(offset));
        offset += 8;
    }

    private static short ReadBigEndianInt16(byte[] buffer, ref int offset)
    {
        short v = System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(buffer.AsSpan(offset));
        offset += 2;
        return v;
    }

    private static int ReadBigEndianInt32(byte[] buffer, ref int offset)
    {
        int v = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(offset));
        offset += 4;
        return v;
    }

    private static long ReadBigEndianInt64(byte[] buffer, ref int offset)
    {
        long v = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(offset));
        offset += 8;
        return v;
    }

    private static float ReadBigEndianFloat32(byte[] buffer, ref int offset)
    {
        float v = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(buffer.AsSpan(offset));
        offset += 4;
        return v;
    }

    private static double ReadBigEndianFloat64(byte[] buffer, ref int offset)
    {
        double v = System.Buffers.Binary.BinaryPrimitives.ReadDoubleBigEndian(buffer.AsSpan(offset));
        offset += 8;
        return v;
    }

    private static int ReadFully(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, total, count - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
