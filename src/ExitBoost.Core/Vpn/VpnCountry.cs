namespace ExitBoost.Core.Vpn;

/// <summary>
/// Определение страны VPN-ключа по его названию: подписки обычно называют
/// точки «🇩🇪 Германия | RAW TCP», поэтому страна берётся из флага, а если флага
/// нет — из названия страны в тексте.
/// </summary>
public static class VpnCountry
{
    /// <summary>Страна из названия ключа; «—», если определить не удалось.</summary>
    public static string Guess(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return "—";

        string name = keyName.Trim();

        // Флаг — это два региональных индикатора (U+1F1E6..U+1F1FF),
        // каждый из которых в UTF-16 занимает сурогатную пару.
        for (int i = 0; i < name.Length;)
        {
            if (!TryReadFlag(name, i, out string flag, out int length)) { i++; continue; }

            if (FlagCountries.TryGetValue(flag, out string? byFlag)) return byFlag;

            string? byText = FromTextAfterFlag(name, i + length);
            if (byText is not null) return byText;
            break;
        }

        return FromKnownCountryName(name) ?? "—";
    }

    private static bool TryReadFlag(string name, int index, out string flag, out int length)
    {
        flag = "";
        length = 0;

        if (index + 3 >= name.Length) return false;
        if (!char.IsHighSurrogate(name[index]) || !char.IsLowSurrogate(name[index + 1]) ||
            !char.IsHighSurrogate(name[index + 2]) || !char.IsLowSurrogate(name[index + 3]))
            return false;

        int first = char.ConvertToUtf32(name[index], name[index + 1]);
        int second = char.ConvertToUtf32(name[index + 2], name[index + 3]);
        if (!IsRegionalIndicator(first) || !IsRegionalIndicator(second)) return false;

        flag = name.Substring(index, 4);
        length = 4;
        return true;
    }

    private static readonly (string Code, string Country)[] CountryCodes =
    [
        ("DE", "Германия"), ("FI", "Финляндия"), ("NL", "Нидерланды"), ("PL", "Польша"),
        ("LV", "Латвия"), ("EE", "Эстония"), ("SE", "Швеция"), ("FR", "Франция"),
        ("TR", "Турция"), ("US", "США"), ("GB", "Великобритания"), ("ES", "Испания"),
        ("IT", "Италия"), ("CA", "Канада"), ("JP", "Япония"), ("CN", "Китай"),
        ("IN", "Индия"), ("BR", "Бразилия"), ("UA", "Украина"), ("KZ", "Казахстан"),
        ("GE", "Грузия"), ("AM", "Армения"), ("RS", "Сербия"), ("CZ", "Чехия"),
        ("AT", "Австрия"), ("CH", "Швейцария"), ("BE", "Бельгия"), ("GR", "Греция"),
        ("IL", "Израиль"), ("CY", "Кипр"), ("MT", "Мальта"), ("RO", "Румыния"),
        ("HU", "Венгрия"), ("SK", "Словакия"), ("DK", "Дания"), ("NO", "Норвегия"),
        ("BG", "Болгария"), ("AL", "Албания"), ("IS", "Исландия"), ("LU", "Люксембург"),
        ("MC", "Монако"), ("VN", "Вьетнам"), ("SG", "Сингапур"), ("HK", "Гонконг"),
        ("MY", "Малайзия"), ("TH", "Таиланд"), ("AE", "ОАЭ"), ("IR", "Иран"),
        ("MD", "Молдова"), ("BY", "Беларусь"), ("UZ", "Узбекистан"),
        ("AZ", "Азербайджан"), ("CL", "Чили"), ("AR", "Аргентина"), ("ZA", "ЮАР"),
        ("NZ", "Новая Зеландия"), ("AU", "Австралия"), ("MX", "Мексика"), ("PT", "Португалия"),
        ("IE", "Ирландия")
    ];

    /// <summary>Страна по флагу (двум буквам кода страны).</summary>
    private static readonly Dictionary<string, string> FlagCountries =
        CountryCodes.ToDictionary(c => FlagFromCode(c.Code), c => c.Country);

    /// <summary>Код страны по русскому названию → флаг.</summary>
    private static readonly Dictionary<string, string> CountryFlags =
        CountryCodes.ToDictionary(c => c.Country, c => FlagFromCode(c.Code), StringComparer.OrdinalIgnoreCase);

    /// <summary>Флаг страны по её названию («Германия» → 🇩🇪); пустая строка, если страна неизвестна.</summary>
    public static string FlagFor(string? country)
        => country is not null && CountryFlags.TryGetValue(country, out string? flag) ? flag : "";

    /// <summary>Двухбуквенный код страны по названию («Германия» → «DE»).</summary>
    public static string CodeFor(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return "";
        foreach (var (code, name) in CountryCodes)
            if (string.Equals(name, country, StringComparison.OrdinalIgnoreCase)) return code;
        return "";
    }

    /// <summary>Собрать флаг по двухбуквенному коду страны (например, «DE»).</summary>
    public static string FlagFromCode(string code)
    {
        if (code.Length != 2) return "";
        return char.ConvertFromUtf32(0x1F1E6 + (code[0] - 'A'))
             + char.ConvertFromUtf32(0x1F1E6 + (code[1] - 'A'));
    }

    private static string? FromTextAfterFlag(string name, int start)
    {
        while (start < name.Length && char.IsWhiteSpace(name[start])) start++;
        int end = start;
        while (end < name.Length && char.IsLetter(name[end])) end++;
        string word = name[start..end].Trim();
        if (word.Length == 0) return null;
        return KnownCountries.Contains(word) ? word : null;
    }

    private static string? FromKnownCountryName(string name)
    {
        foreach (string country in KnownCountries)
            if (name.Contains(country, StringComparison.OrdinalIgnoreCase))
                return country;
        return null;
    }

    private static bool IsRegionalIndicator(int codePoint) =>
        codePoint >= 0x1F1E6 && codePoint <= 0x1F1FF;

    /// <summary>Частые названия стран в названиях точек (русские и английские).</summary>
    private static readonly string[] KnownCountries =
    [
        "Россия", "Германия", "Финляндия", "Нидерланды", "Польша", "Латвия", "Эстония",
        "Швеция", "Франция", "Турция", "США", "Америка", "Великобритания", "Англия",
        "Испания", "Италия", "Канада", "Япония", "Китай", "Индия", "Бразилия",
        "Украина", "Казахстан", "Грузия", "Армения", "Сербия", "Чехия", "Австрия",
        "Швейцария", "Бельгия", "Греция", "Израиль", "Кипр", "Мальта", "Румыния",
        "Венгрия", "Словакия", "Дания", "Норвегия", "Болгария", "Албания", "Исландия",
        "Люксембург", "Монако", "Вьетнам", "Сингапур", "Гонконг", "Малайзия", "Таиланд",
        "Russia", "Germany", "Finland", "Netherlands", "Poland", "Latvia", "Estonia",
        "Sweden", "France", "Turkey", "USA", "America", "United Kingdom", "England",
        "Spain", "Italy", "Canada", "Japan", "China", "India", "Brazil", "Ukraine",
        "Kazakhstan", "Georgia", "Armenia", "Serbia", "Czech", "Austria", "Switzerland",
        "Belgium", "Greece", "Israel", "Cyprus", "Malta", "Romania", "Hungary",
        "Slovakia", "Denmark", "Norway", "Bulgaria", "Albania", "Iceland", "Luxembourg",
        "Monaco", "Vietnam", "Singapore", "Hong Kong", "Malaysia", "Thailand"
    ];
}
