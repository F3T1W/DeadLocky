using System.Globalization;

namespace DeadLocky.Features.UpdateApp.Model;

public sealed record AppVersion(int Major, int Minor, int Patch, string Preview = "") : IComparable<AppVersion>
{
    public static AppVersion? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string text = value.StartsWith('v') ? value[1..] : value;
        string[] metadata = text.Split('+', 2);
        string[] version = metadata[0].Split('-', 2);
        string[] numbers = version[0].Split('.');
        if (numbers.Length != 3 || !Number(numbers[0], out int major)
                                || !Number(numbers[1], out int minor) || !Number(numbers[2], out int patch))
        {
            return null;
        }

        string preview = version.Length == 2 ? version[1] : "";
        return (version.Length == 2 && !Identifiers(preview, true))
            || (metadata.Length == 2 && !Identifiers(metadata[1], false))
            ? null
            : new AppVersion(major, minor, patch, preview);
    }

    private static bool Number(string text, out int number)
    {
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number)
               && (text.Length == 1 || text[0] != '0');
    }

    private static bool Identifiers(string value, bool preview)
    {
        return value.Split('.').All(part => part.Length > 0
            && part.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-')
            && (!preview || !part.All(char.IsAsciiDigit) || part.Length == 1 || part[0] != '0'));
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0 || Preview == other.Preview)
        {
            return core;
        }

        if (Preview.Length == 0 || other.Preview.Length == 0)
        {
            return Preview.Length == 0 ? 1 : -1;
        }

        string[] left = Preview.Split('.');
        string[] right = other.Preview.Split('.');
        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            bool a = left[i].All(char.IsAsciiDigit);
            bool b = right[i].All(char.IsAsciiDigit);
            int order = (a, b) switch
            {
                (true, true) => left[i].Length == right[i].Length
                    ? string.CompareOrdinal(left[i], right[i]) : left[i].Length.CompareTo(right[i].Length),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(left[i], right[i])
            };
            if (order != 0)
            {
                return order;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    public static bool operator <(AppVersion? left, AppVersion? right)
    {
        return Comparer<AppVersion>.Default.Compare(left, right) < 0;
    }

    public static bool operator >(AppVersion? left, AppVersion? right)
    {
        return Comparer<AppVersion>.Default.Compare(left, right) > 0;
    }

    public static bool operator <=(AppVersion? left, AppVersion? right)
    {
        return Comparer<AppVersion>.Default.Compare(left, right) <= 0;
    }

    public static bool operator >=(AppVersion? left, AppVersion? right)
    {
        return Comparer<AppVersion>.Default.Compare(left, right) >= 0;
    }

    public override string ToString()
    {
        return FormattableString.Invariant($"{Major}.{Minor}.{Patch}") + (Preview.Length == 0 ? "" : $"-{Preview}");
    }
}
