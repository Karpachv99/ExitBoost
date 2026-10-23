using System.Text.Json;

namespace ExitBoost.Core.Dpi;

/// <summary>
/// Каталог стратегий обхода DPI. Основа — пресеты Flowseal (zapret-discord-youtube):
/// многопрофильные наборы winws.exe с фейковыми пакетами TCP/QUIC и голосовым UDP Discord.
/// </summary>
public sealed class ZapretCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Version { get; set; } = "встроенная";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<DpiStrategy> Strategies { get; set; } = new();

    public string FilePath { get; set; } = "";

    /// <summary>Базовый набор стратегий — пресет Flowseal.</summary>
    public static ZapretCatalog BuildDefault()
    {
        var catalog = new ZapretCatalog();
        AddFlowseal(catalog);
        return catalog;
    }

    /// <summary>
    /// Пресеты из Flowseal zapret-discord-youtube (обёртка над тем же winws.exe).
    /// Многопрофильные наборы: обман TCP-страниц + фейковые QUIC-пакеты (UDP 443)
    /// + голосовой UDP Discord. Фейковые файлы берутся из папки fakes установщика.
    /// Подстановки: %HOSTLIST% — список доменов, %FAKES% — папка с фейковыми .bin.
    /// </summary>
    private static void AddFlowseal(ZapretCatalog catalog)
    {
        // Хвостовое ядро: фейковые QUIC-пакеты (UDP 443) + голосовой UDP Discord.
        string[] UdpTail(bool withDiscord) => withDiscord
            ? new[]
              {
                "--wf-udp=443,19294-19344,50000-50100",
                "--new",
                "--filter-udp=443", "--hostlist=%HOSTLIST%",
                "--dpi-desync=fake", "--dpi-desync-repeats=11",
                "--new",
                "--filter-udp=19294-19344,50000-50100", "--filter-l7=discord,stun",
                "--dpi-desync=fake", "--dpi-desync-repeats=6"
              }
            : new[]
              {
                "--wf-udp=443",
                "--new",
                "--filter-udp=443", "--hostlist=%HOSTLIST%",
                "--dpi-desync=fake", "--dpi-desync-repeats=11"
              };

        var list = new List<(string Id, string Name, string Desc, string[] Head, bool Discord)>

        {
            ("fs_multisplit_google", "Flowseal: дробление с подложкой google",
                "Пресет «ALT6»: запрос дробится, перед ним подкладывается непонятный кусок из фейкового ClientHello google.com. Один из самых надёжных пресетов Flowseal.",
                new[]
                {
                    "--dpi-desync=multisplit",
                    "--dpi-desync-split-seqovl=681",
                    "--dpi-desync-split-pos=1",
                    "--dpi-desync-split-seqovl-pattern=%FAKES%tls_clienthello_www_google_com.bin"
                }, true),

            ("fs_hostfakesplit_google", "Flowseal: подмена SNI на google.com",
                "Пресет «ALT3»: фейковый пакет с заменой имени сайта на www.google.com внутри TLS-заголовка. Плюс QUIC-фейк и голосовой UDP Discord.",
                new[]
                {
                    "--dpi-desync=fake,hostfakesplit",
                    "--dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com",
                    "--dpi-desync-hostfakesplit-mod=host=www.google.com,altorder=1",
                    "--dpi-desync-fooling=ts"
                }, true),

            ("fs_fake_multisplit_sferum", "Flowseal: фейк + дробление (подложка sferum)",
                "Пресет «ALT11»: дробление с перекрытием из ClientHello sferum.ru — вариант, когда google-подложка не сработала.",
                new[]
                {
                    "--dpi-desync=fake,multisplit",
                    "--dpi-desync-split-seqovl=664",
                    "--dpi-desync-split-pos=1",
                    "--dpi-desync-fooling=ts",
                    "--dpi-desync-repeats=8",
                    "--dpi-desync-split-seqovl-pattern=%FAKES%tls_clienthello_www_sferum_ru.bin"
                }, true),

            ("fs_fakedsplit_badseq", "Flowseal: разрыв + сдвиг номера пакета",
                "Пресет «FAKE TLS AUTO ALT»: фейк со случайным TLS, разрыв в начале запроса и сдвиг номера пакета (badseq) — самый живучий приём из пресетов.",
                new[]
                {
                    "--dpi-desync=fake,fakedsplit",
                    "--dpi-desync-split-pos=1",
                    "--dpi-desync-fooling=badseq",
                    "--dpi-desync-badseq-increment=2",
                    "--dpi-desync-repeats=8",
                    "--dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com"
                }, true),

            ("fs_fakedsplit_ts", "Flowseal: разрыв с обманом таймстэмпов",
                "Пресет «ALT»: фейк с разрывом внутри запроса и обманом через TCP-таймстэмпы — DPI теряет ориентацию в соединении.",
                new[]
                {
                    "--dpi-desync=fake,fakedsplit",
                    "--dpi-desync-repeats=6",
                    "--dpi-desync-fooling=ts",
                    "--dpi-desync-fakedsplit-pattern=0x00"
                }, true),

            ("fs_fake_badseq", "Flowseal: чистый фейк + сдвиг пакета",
                "Пресет «ALT8»: фейковый пакет без подмен TLS и сдвиг номера пакета. Самый простой и универсальный пресет.",
                new[]
                {
                    "--dpi-desync=fake",
                    "--dpi-desync-fake-tls-mod=none",
                    "--dpi-desync-repeats=6",
                    "--dpi-desync-fooling=badseq",
                    "--dpi-desync-badseq-increment=2"
                }, true),

            ("fs_hostfakesplit_ozon", "Flowseal: подмена SNI на ozon.ru",
                "Пресет «ALT9»: подменное имя ozon.ru с тройным обманом (ts + md5sig). Помогает, когда RTP-подмены Google/Mail уже знакомы провайдеру.",
                new[]
                {
                    "--dpi-desync=hostfakesplit",
                    "--dpi-desync-repeats=4",
                    "--dpi-desync-fooling=ts,md5sig",
                    "--dpi-desync-hostfakesplit-mod=host=ozon.ru"
                }, true),

            ("fs_fake_multisplit_badseq10m", "Flowseal: дробление + большой сдвиг номера",
                "Пресет «FAKE TLS AUTO ALT2»: дробление с подложкой google и экстремальным сдвигом номера пакета (badseq 10000000) — сильный сбивающий приём.",
                new[]
                {
                    "--dpi-desync=fake,multisplit",
                    "--dpi-desync-split-seqovl=681",
                    "--dpi-desync-split-pos=1",
                    "--dpi-desync-fooling=badseq",
                    "--dpi-desync-badseq-increment=10000000",
                    "--dpi-desync-repeats=8",
                    "--dpi-desync-split-seqovl-pattern=%FAKES%tls_clienthello_www_google_com.bin",
                    "--dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com"
                }, true),

            ("fs_fake_multisplit_ts", "Flowseal: дробление + таймстэмпы",
                "Пресет «FAKE TLS AUTO ALT3»: то же дробление с подложкой google, но обман через таймстэмпы.",
                new[]
                {
                    "--dpi-desync=fake,multisplit",
                    "--dpi-desync-split-seqovl=681",
                    "--dpi-desync-split-pos=1",
                    "--dpi-desync-fooling=ts",
                    "--dpi-desync-repeats=8",
                    "--dpi-desync-split-seqovl-pattern=%FAKES%tls_clienthello_www_google_com.bin",
                    "--dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com"
                }, true),

            ("fs_multisplit_sniext", "Flowseal: разрыв в SNI-расширении",
                "Пресет «ALT7»: разрыв точно внутри TLS-расширения имени сайта (sniext+1) с перекрытием google — разрез попадает ровно туда, где лежит домен.",
                new[]
                {
                    "--dpi-desync=multisplit",
                    "--dpi-desync-split-pos=2,sniext+1",
                    "--dpi-desync-split-seqovl=679",
                    "--dpi-desync-split-seqovl-pattern=%FAKES%tls_clienthello_www_google_com.bin"
                }, true),

            ("fs_fake_multidisorder_badseq", "Flowseal: вразброс + сдвиг пакета",
                "Пресет «FAKE TLS AUTO»: фейк, части запроса вразброс и сдвиг номера пакета. Сильный вариант против YouTube (QUIC + TCP).",
                new[]
                {
                    "--dpi-desync=fake,multidisorder",
                    "--dpi-desync-split-pos=1,midsld",
                    "--dpi-desync-repeats=11",
                    "--dpi-desync-fooling=badseq",
                    "--dpi-desync-fake-tls=0x00000000",
                    "--dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com"
                }, true),

            ("fs_hostfakesplit_ya", "Flowseal: подмена SNI на ya.ru",
                "Пресет «SIMPLE FAKE»: подмена имени сайта на ya.ru. Вариант, если google.com у провайдера под особым вниманием.",
                new[]
                {
                    "--dpi-desync=hostfakesplit",
                    "--dpi-desync-hostfakesplit-mod=host=ya.ru,altorder=1",
                    "--dpi-desync-fooling=ts"
                }, false),

            ("fs_hostfakesplit_mailru", "Flowseal: подмена SNI на mail.ru",
                "Пресет «ALT13»: подменное имя mail.ru и вирус-подложка sochi_park. Ещё одна «запасная» подмена имени сайта.",
                new[]
                {
                    "--dpi-desync=fake,hostfakesplit",
                    "--dpi-desync-fooling=ts",
                    "--dpi-desync-hostfakesplit-mod=host=mail.ru,altorder=1",
                    "--dpi-desync-repeats=5",
                    "--dpi-desync-fake-tls=%FAKES%tls_clienthello_sochi_park.bin"
                }, true),

            ("fs_syndata_multidisorder", "Flowseal: обман на этапе подключения",
                "Пресет «ALT5»: путает DPI ещё на этапе установления соединения (syndata), затем переставляет части запроса.",
                new[]
                {
                    "--dpi-desync=syndata,multidisorder"
                }, false)
        };

        foreach (var (id, name, desc, head, discord) in list)
        {
            var args = head.ToList();
            args.AddRange(UdpTail(discord));
            catalog.Strategies.Add(new DpiStrategy
            {
                Id = id,
                Name = name,
                Description = desc,
                Args = args.ToArray(),
                Source = "Flowseal"
            });
        }
    }

    public void Save(string path)
    {
        FilePath = path;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { }
    }

    public static ZapretCatalog Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var catalog = JsonSerializer.Deserialize<ZapretCatalog>(File.ReadAllText(path), JsonOptions);
                if (catalog is { Strategies.Count: > 0 })
                {
                    catalog.FilePath = path;
                    return catalog;
                }
            }
        }
        catch { }

        var fresh = BuildDefault();
        fresh.FilePath = path;
        return fresh;
    }
}
