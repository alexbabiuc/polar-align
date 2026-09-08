using System.Globalization;

namespace FreePolarAlign.Imaging.Fits;

/// <summary>One FITS header card: an 8-character keyword, an optional typed value, and an optional comment.</summary>
public sealed record FitsCard(string Keyword, object? Value, string? Comment);

/// <summary>
/// An ordered collection of FITS header cards, excluding the structural
/// keywords (SIMPLE, BITPIX, NAXIS, NAXIS1/2, BZERO, BSCALE, END) which
/// <see cref="FitsImage"/>/<see cref="FitsFile"/> manage directly. This is
/// where WCS keywords (CRPIX/CRVAL/CD.../CTYPE) and any other metadata live.
/// </summary>
public sealed class FitsHeader
{
    private readonly List<FitsCard> _cards = new();

    public IReadOnlyList<FitsCard> Cards => _cards;

    public void Set(string keyword, object value, string? comment = null)
    {
        keyword = keyword.ToUpperInvariant();
        int existing = _cards.FindIndex(c => c.Keyword == keyword);
        var card = new FitsCard(keyword, value, comment);
        if (existing >= 0)
        {
            _cards[existing] = card;
        }
        else
        {
            _cards.Add(card);
        }
    }

    public bool TryGet(string keyword, out object? value)
    {
        keyword = keyword.ToUpperInvariant();
        foreach (var card in _cards)
        {
            if (card.Keyword == keyword)
            {
                value = card.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    public double GetDouble(string keyword)
    {
        if (!TryGet(keyword, out object? value) || value is null)
        {
            throw new KeyNotFoundException($"FITS header has no keyword '{keyword}'.");
        }

        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    public double GetDouble(string keyword, double defaultValue) =>
        TryGet(keyword, out object? value) && value is not null ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : defaultValue;

    public string GetString(string keyword) =>
        TryGet(keyword, out object? value) && value is string s ? s : throw new KeyNotFoundException($"FITS header has no string keyword '{keyword}'.");

    public string GetString(string keyword, string defaultValue) =>
        TryGet(keyword, out object? value) && value is string s ? s : defaultValue;
}
