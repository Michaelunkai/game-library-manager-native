using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GameLibrary.Native;

public static class TagKinds
{
    public const string Game = "Game";
    public const string Movie = "Movie";
    public const string Tv = "Tv";
    public const string Documentary = "Documentary";
    public const string Anime = "Anime";
    public const string Audio = "Audio";
    public const string Book = "Book";
    public const string Software = "Software";
    public const string Dlc = "Dlc";
    public const string Mod = "Mod";
    public const string Rom = "Rom";
    public const string Other = "Other";
}

public sealed record TagClassification(
    string Tag,
    string Kind,
    string TargetCategoryId,
    string Rationale,
    bool HeuristicOnly = false);

public sealed record TagMove(
    string Tag,
    string From,
    string To,
    string Kind,
    string Rationale);

public static class TagTaxonomy
{
    public const string MoviesCategoryId = "hidden-movies";
    public const string TvCategoryId = "hidden-tv";
    public const string AnimeCategoryId = "hidden-anime";
    public const string AudioCategoryId = "hidden-audio";
    public const string BooksCategoryId = "hidden-books";
    public const string SoftwareCategoryId = "hidden-software";
    public const string OtherCategoryId = "hidden-other";
    public const string VisibleSourceId = "visible";

    public static readonly IReadOnlyList<string> DestinationCategoryIds = new[]
    {
        MoviesCategoryId, TvCategoryId, AnimeCategoryId, AudioCategoryId, BooksCategoryId, SoftwareCategoryId, OtherCategoryId
    };

    private static readonly HashSet<string> DestinationSet = new(DestinationCategoryIds, StringComparer.Ordinal);
    private static readonly IReadOnlyList<TagClassification> Entries = BuildCatalog();
    private static readonly IReadOnlyDictionary<string, TagClassification> Index =
        Entries.ToDictionary(entry => entry.Tag, StringComparer.Ordinal);
    private static readonly HashSet<string> GameTagSet = BuildGameTags();

    public static IReadOnlyList<TagClassification> Catalog() => Entries;

    public static bool TryClassify(string tag, out TagClassification? classification)
    {
        if (Index.TryGetValue(Normalize(tag), out TagClassification? found))
        {
            classification = found;
            return true;
        }
        classification = null;
        return false;
    }

    public static bool IsHeuristicOnly(string tag)
        => Index.TryGetValue(Normalize(tag), out TagClassification? found) && found.HeuristicOnly;

    public static bool IsGameTag(string tag)
    {
        string normalized = Normalize(tag);
        return normalized.Length > 0 && GameTagSet.Contains(normalized);
    }

    public static bool IsDestinationCategoryId(string categoryId)
        => DestinationSet.Contains(categoryId);

    public static IReadOnlyList<TagMove> PlanMoves(IEnumerable<string> existingTags, IReadOnlySet<string> hiddenCategoryIds)
        => PlanMoves(existingTags, hiddenCategoryIds, includeHeuristics: false);

    public static IReadOnlyList<TagMove> PlanMoves(IEnumerable<string> existingTags, IReadOnlySet<string> hiddenCategoryIds, bool includeHeuristics)
    {
        ArgumentNullException.ThrowIfNull(existingTags);
        ArgumentNullException.ThrowIfNull(hiddenCategoryIds);

        var moves = new List<TagMove>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in existingTags)
        {
            string tag = Normalize(raw);
            if (tag.Length == 0 || !seen.Add(tag)) continue;
            if (IsGameTag(tag)) continue;
            if (IsDestinationCategoryId(tag)) continue;
            if (!Index.TryGetValue(tag, out TagClassification? classification) || classification is null) continue;
            if (classification.HeuristicOnly && !includeHeuristics) continue;
            if (!hiddenCategoryIds.Contains(classification.TargetCategoryId)) continue;
            moves.Add(new TagMove(tag, VisibleSourceId, classification.TargetCategoryId, classification.Kind, classification.Rationale));
        }
        return moves;
    }

    public static string DescribePlan(IEnumerable<TagMove> moves)
    {
        ArgumentNullException.ThrowIfNull(moves);
        TagMove[] list = moves.ToArray();
        var text = new StringBuilder();
        text.Append("Tag taxonomy plan: ").Append(list.Length).Append(" tag(s) to move into hidden categories.");
        foreach (var group in list.GroupBy(move => move.To, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            text.Append(Environment.NewLine)
                .Append("  ").Append(group.Key).Append(" (").Append(group.Count()).Append("): ")
                .Append(string.Join(", ", group.Select(move => move.Tag).OrderBy(tag => tag, StringComparer.Ordinal)));
        }
        if (list.Length == 0) text.Append(Environment.NewLine).Append("  (nothing to move)");
        return text.ToString();
    }

    public static string Normalize(string? tag)
        => string.IsNullOrWhiteSpace(tag) ? "" : tag.Trim().ToLowerInvariant();

    private static string DestinationFor(string kind) => kind switch
    {
        TagKinds.Movie => MoviesCategoryId,
        TagKinds.Documentary => MoviesCategoryId,
        TagKinds.Tv => TvCategoryId,
        TagKinds.Anime => AnimeCategoryId,
        TagKinds.Audio => AudioCategoryId,
        TagKinds.Book => BooksCategoryId,
        TagKinds.Software => SoftwareCategoryId,
        TagKinds.Dlc => OtherCategoryId,
        TagKinds.Mod => OtherCategoryId,
        TagKinds.Rom => OtherCategoryId,
        TagKinds.Other => OtherCategoryId,
        _ => throw new InvalidOperationException($"Kind '{kind}' has no hidden destination category.")
    };

    private static IReadOnlyList<TagClassification> BuildCatalog()
    {
        var list = new List<TagClassification>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string tag, string kind, string rationale, bool heuristic = false)
        {
            string normalized = Normalize(tag);
            if (!seen.Add(normalized)) throw new InvalidOperationException($"Duplicate taxonomy tag '{normalized}'.");
            list.Add(new TagClassification(normalized, kind, DestinationFor(kind), rationale, heuristic));
        }

        Add("movie", TagKinds.Movie, "Names a film, not an interactive game.");
        Add("movies", TagKinds.Movie, "Plural of movie; film content only.");
        Add("film", TagKinds.Movie, "Film is a non-interactive motion picture.");
        Add("films", TagKinds.Movie, "Plural of film; motion pictures, not games.");
        Add("cinema", TagKinds.Movie, "Cinema denotes theatrical film content.");
        Add("dvdr", TagKinds.Movie, "DVD-R is an optical film-release format.");
        Add("dvdrip", TagKinds.Movie, "Rip naming for a film released on DVD.");
        Add("bluray", TagKinds.Movie, "Blu-ray is a film/media disc format.");
        Add("blu-ray", TagKinds.Movie, "Blu-ray disc format used for films.");
        Add("bdrip", TagKinds.Movie, "Blu-ray rip naming from film releases.");
        Add("brrip", TagKinds.Movie, "Blu-ray rip naming from film releases.");
        Add("uhd", TagKinds.Movie, "UHD is an ultra-high-definition film video format.");
        Add("4k", TagKinds.Movie, "4K is a film/video resolution marker.");
        Add("2160p", TagKinds.Movie, "2160p is a film/video resolution marker.");
        Add("1080p", TagKinds.Movie, "1080p is a film/video resolution marker.");
        Add("720p", TagKinds.Movie, "720p is a film/video resolution marker.");
        Add("480p", TagKinds.Movie, "480p is a film/video resolution marker.");
        Add("x264", TagKinds.Movie, "x264 is a film/video codec name.");
        Add("x265", TagKinds.Movie, "x265 is a film/video codec name.");
        Add("h264", TagKinds.Movie, "H.264 is a film/video codec name.");
        Add("h265", TagKinds.Movie, "H.265 is a film/video codec name.");
        Add("hevc", TagKinds.Movie, "HEVC is a film/video codec name.");
        Add("avc", TagKinds.Movie, "AVC is a film/video codec name.");
        Add("remux", TagKinds.Movie, "Remux describes a lossless film repackaging.");
        Add("webrip", TagKinds.Movie, "WEBRip names a film release source.");
        Add("web-dl", TagKinds.Movie, "WEB-DL names a film download release.");
        Add("webdl", TagKinds.Movie, "WEB-DL variant naming a film release.");
        Add("hdrip", TagKinds.Movie, "HDRip names a film release source.");
        Add("camrip", TagKinds.Movie, "CAMRip names a camcorded film release.");
        Add("cam", TagKinds.Movie, "CAM names a camcorded film release.");
        Add("hdcam", TagKinds.Movie, "HDCAM names a camcorded film release.");
        Add("dvdscr", TagKinds.Movie, "DVDSCR names a film screener release.");
        Add("screener", TagKinds.Movie, "Screener names a pre-release film copy.");
        Add("telesync", TagKinds.Movie, "Telesync names a film release source.");
        Add("telecine", TagKinds.Movie, "Telecine names a film release source.");
        Add("xvid", TagKinds.Movie, "XviD is a film/video codec name.");
        Add("divx", TagKinds.Movie, "DivX is a film/video codec name.");
        Add("imax", TagKinds.Movie, "IMAX is a theatrical film format.");
        Add("unrated", TagKinds.Movie, "Unrated is a film certification variant.");
        Add("extended", TagKinds.Movie, "Extended is a film cut variant.");
        Add("directors-cut", TagKinds.Movie, "Director's cut is a film edition.");
        Add("trilogy", TagKinds.Movie, "Trilogy groups films, not games.");
        Add("saga", TagKinds.Movie, "Saga groups films in a franchise.");
        Add("feature-film", TagKinds.Movie, "Feature film is a motion picture.");
        Add("short-film", TagKinds.Movie, "Short film is a motion picture.");
        Add("3d-movie", TagKinds.Movie, "3D movie is a theatrical film format.");
        Add("movie-pack", TagKinds.Movie, "Movie pack bundles films.");
        Add("movie-collection", TagKinds.Movie, "Movie collection bundles films.");
        Add("ts", TagKinds.Movie, "Scene TS means telesync film capture; weak signal because TS can mean TypeScript or team speak.", heuristic: true);
        Add("tc", TagKinds.Movie, "Scene TC means telecine film capture; weak/ambiguous acronym.", heuristic: true);
        Add("r5", TagKinds.Movie, "R5 is a film release region marker; weak/heuristic signal.", heuristic: true);
        Add("proper", TagKinds.Movie, "PROPER is a film release correction marker; weak/heuristic because it can be a game patch label.", heuristic: true);
        Add("repack", TagKinds.Movie, "REPACK is a film release correction marker; weak/heuristic because game repacks share the term.", heuristic: true);
        Add("internal", TagKinds.Movie, "INTERNAL is a film release group marker; weak/heuristic because it can appear on any build.", heuristic: true);
        Add("limited", TagKinds.Movie, "LIMITED is a film distribution marker; weak/heuristic.", heuristic: true);
        Add("remastered", TagKinds.Movie, "Remastered is a film reissue marker; weak/heuristic because games are also remastered.", heuristic: true);
        Add("complete", TagKinds.Movie, "COMPLETE often marks a finished film/series set; weak/heuristic because games can be complete editions.", heuristic: true);
        Add("collection", TagKinds.Movie, "COLLECTION often marks a film bundle; weak/heuristic because games also ship collections.", heuristic: true);

        Add("documentary", TagKinds.Documentary, "Documentary is non-fiction film/TV, not a game.");
        Add("docu", TagKinds.Documentary, "Docu is shorthand for documentary film/TV.");
        Add("nature-documentary", TagKinds.Documentary, "Nature documentary is non-fiction film/TV.");
        Add("true-crime", TagKinds.Documentary, "True-crime is non-fiction documentary programming.");
        Add("docufilm", TagKinds.Documentary, "Docufilm is a documentary motion picture.");

        Add("tv", TagKinds.Tv, "TV denotes television programming.");
        Add("tvshow", TagKinds.Tv, "TV show denotes an episodic television series.");
        Add("tv-show", TagKinds.Tv, "TV-show denotes an episodic television series.");
        Add("tvseries", TagKinds.Tv, "TV series denotes episodic television.");
        Add("tv-series", TagKinds.Tv, "TV-series denotes episodic television.");
        Add("series", TagKinds.Tv, "Series denotes episodic television programming.");
        Add("season", TagKinds.Tv, "Season is a television episode grouping.");
        Add("seasons", TagKinds.Tv, "Seasons group television episodes.");
        Add("episode", TagKinds.Tv, "Episode is a television installment.");
        Add("episodes", TagKinds.Tv, "Episodes are television installments.");
        Add("complete-series", TagKinds.Tv, "Complete series is a full television run.");
        Add("complete-season", TagKinds.Tv, "Complete season is a full television season.");
        Add("season-pack", TagKinds.Tv, "Season pack bundles television episodes.");
        Add("miniseries", TagKinds.Tv, "Miniseries is a short television series.");
        Add("mini-series", TagKinds.Tv, "Mini-series is a short television series.");
        Add("sitcom", TagKinds.Tv, "Sitcom is a television comedy genre.");
        Add("docuseries", TagKinds.Tv, "Docuseries is episodic documentary television.");
        Add("reality-tv", TagKinds.Tv, "Reality TV is television programming.");
        Add("talk-show", TagKinds.Tv, "Talk show is television programming.");
        Add("web-series", TagKinds.Tv, "Web series is episodic online programming.");
        Add("show", TagKinds.Tv, "Show denotes a television program.");
        Add("shows", TagKinds.Tv, "Shows denote television programs.");
        Add("soap-opera", TagKinds.Tv, "Soap opera is television programming.");
        Add("batch", TagKinds.Tv, "Batch typically bundles a series run; weak/heuristic because anime and games use the term.", heuristic: true);

        Add("anime", TagKinds.Anime, "Anime is Japanese animation, not an interactive game.");
        Add("donghua", TagKinds.Anime, "Donghua is Chinese animation.");
        Add("hentai", TagKinds.Anime, "Hentai is adult animation.");
        Add("ona", TagKinds.Anime, "ONA is original net animation.");
        Add("ova", TagKinds.Anime, "OVA is original video animation.");
        Add("oad", TagKinds.Anime, "OAD is original animation DVD.");
        Add("dub", TagKinds.Anime, "Dub marks an anime audio track, not a game.");
        Add("sub", TagKinds.Anime, "Sub marks an anime subtitle track; weak because sub can mean substitute.", heuristic: true);
        Add("subs", TagKinds.Anime, "Subs marks anime subtitle tracks.");
        Add("dual-audio", TagKinds.Anime, "Dual audio marks an anime release audio layout.");
        Add("subbed", TagKinds.Anime, "Subbed marks subtitled anime content.");
        Add("fansub", TagKinds.Anime, "Fansub marks fan-subtitled anime content.");
        Add("raw", TagKinds.Anime, "Raw marks unsubtitled anime source video.");
        Add("anime-batch", TagKinds.Anime, "Anime batch bundles anime episodes.");

        Add("soundtrack", TagKinds.Audio, "Soundtrack is music, not an interactive game.");
        Add("soundtracks", TagKinds.Audio, "Soundtracks are music releases.");
        Add("ost", TagKinds.Audio, "OST is an original soundtrack album.");
        Add("original-soundtrack", TagKinds.Audio, "Original soundtrack is a music album.");
        Add("original-motion-picture-soundtrack", TagKinds.Audio, "OMPS is a film music album.");
        Add("score", TagKinds.Audio, "Score is instrumental music, not a game.");
        Add("flac", TagKinds.Audio, "FLAC is a lossless audio format.");
        Add("aac", TagKinds.Audio, "AAC is an audio codec.");
        Add("dts", TagKinds.Audio, "DTS is an audio codec.");
        Add("truehd", TagKinds.Audio, "TrueHD is an audio codec.");
        Add("atmos", TagKinds.Audio, "Dolby Atmos is an audio format.");
        Add("mp3", TagKinds.Audio, "MP3 is an audio format.");
        Add("m4a", TagKinds.Audio, "M4A is an audio container.");
        Add("wav", TagKinds.Audio, "WAV is an audio format.");
        Add("ogg", TagKinds.Audio, "OGG is an audio format.");
        Add("opus", TagKinds.Audio, "Opus is an audio codec.");
        Add("album", TagKinds.Audio, "Album is a music release.");
        Add("albums", TagKinds.Audio, "Albums are music releases.");
        Add("music", TagKinds.Audio, "Music is audio content, not a game.");
        Add("audio", TagKinds.Audio, "Audio is sound content, not a game.");
        Add("ost-collection", TagKinds.Audio, "OST collection bundles soundtrack albums.");
        Add("game-ost", TagKinds.Audio, "A game OST is still a music album, not the game.");
        Add("remastered-audio", TagKinds.Audio, "Remastered audio is a music reissue.");
        Add("vinyl", TagKinds.Audio, "Vinyl is a music release format.");
        Add("lossless", TagKinds.Audio, "Lossless is an audio quality marker.");
        Add("320kbps", TagKinds.Audio, "320kbps is an audio bitrate marker.");
        Add("mixtape", TagKinds.Audio, "Mixtape is a music release.");
        Add("live-album", TagKinds.Audio, "Live album is a music release.");
        Add("single", TagKinds.Audio, "Single is a music release; weak/heuristic because it is a generic word.", heuristic: true);
        Add("ep", TagKinds.Audio, "EP is a music release; weak/heuristic because EP can mean episode.", heuristic: true);
        Add("bootleg", TagKinds.Audio, "Bootleg is an unofficial music release; weak/heuristic.", heuristic: true);

        Add("ebook", TagKinds.Book, "Ebook is a digital book.");
        Add("e-book", TagKinds.Book, "E-book is a digital book.");
        Add("epub", TagKinds.Book, "EPUB is an ebook format.");
        Add("mobi", TagKinds.Book, "MOBI is an ebook format.");
        Add("azw", TagKinds.Book, "AZW is an Amazon ebook format.");
        Add("azw3", TagKinds.Book, "AZW3 is an Amazon ebook format.");
        Add("pdf", TagKinds.Book, "PDF is a document/book format.");
        Add("magazine", TagKinds.Book, "Magazine is periodical print content.");
        Add("comic", TagKinds.Book, "Comic is sequential-art reading material.");
        Add("comics", TagKinds.Book, "Comics are sequential-art reading material.");
        Add("manga", TagKinds.Book, "Manga is Japanese comic reading material.");
        Add("manhwa", TagKinds.Book, "Manhwa is Korean comic reading material.");
        Add("manhua", TagKinds.Book, "Manhua is Chinese comic reading material.");
        Add("light-novel", TagKinds.Book, "Light novel is prose fiction.");
        Add("novel", TagKinds.Book, "Novel is prose fiction.");
        Add("book", TagKinds.Book, "Book is reading material.");
        Add("books", TagKinds.Book, "Books are reading material.");
        Add("vols", TagKinds.Book, "Vols abbreviates book/comic volumes.");
        Add("volume", TagKinds.Book, "Volume is a book/comic installment.");
        Add("volumes", TagKinds.Book, "Volumes are book/comic installments.");
        Add("cbz", TagKinds.Book, "CBZ is a comic archive format.");
        Add("cbr", TagKinds.Book, "CBR is a comic archive format.");
        Add("cb7", TagKinds.Book, "CB7 is a comic archive format.");
        Add("djvu", TagKinds.Book, "DjVu is a scanned document format.");
        Add("audiobook", TagKinds.Book, "Audiobook is spoken-word book content.");
        Add("audio-book", TagKinds.Book, "Audio book is spoken-word book content.");
        Add("artbook", TagKinds.Book, "Artbook is a printed/visual book.");
        Add("art-book", TagKinds.Book, "Art book is a printed/visual book.");
        Add("strategy-guide", TagKinds.Book, "Strategy guide is a companion book; weak/heuristic because it relates to games.", heuristic: true);
        Add("graphic-novel", TagKinds.Book, "Graphic novel is sequential-art reading material.");
        Add("comic-book", TagKinds.Book, "Comic book is sequential-art reading material.");
        Add("textbook", TagKinds.Book, "Textbook is educational reading material.");

        Add("application", TagKinds.Software, "Application is software, not a game.");
        Add("applications", TagKinds.Software, "Applications are software programs.");
        Add("app", TagKinds.Software, "App is a software program.");
        Add("apps", TagKinds.Software, "Apps are software programs.");
        Add("software", TagKinds.Software, "Software is non-game program content.");
        Add("program", TagKinds.Software, "Program is software.");
        Add("utility", TagKinds.Software, "Utility is a software tool.");
        Add("utilities", TagKinds.Software, "Utilities are software tools.");
        Add("crack", TagKinds.Software, "Crack is a software modification tool.");
        Add("cracks", TagKinds.Software, "Cracks are software modification tools.");
        Add("keygen", TagKinds.Software, "Keygen generates software license keys.");
        Add("serial", TagKinds.Software, "Serial is a software license key.");
        Add("serials", TagKinds.Software, "Serials are software license keys.");
        Add("iso", TagKinds.Software, "ISO is a disc image, not a game title.");
        Add("bootable", TagKinds.Software, "Bootable marks bootable software media.");
        Add("tools", TagKinds.Software, "Tools are software utilities.");
        Add("tool", TagKinds.Software, "Tool is a software utility.");
        Add("script", TagKinds.Software, "Script is executable code.");
        Add("scripts", TagKinds.Software, "Scripts are executable code.");
        Add("driver", TagKinds.Software, "Driver is system software.");
        Add("drivers", TagKinds.Software, "Drivers are system software.");
        Add("firmware", TagKinds.Software, "Firmware is device software.");
        Add("portable", TagKinds.Software, "Portable marks portable software.");
        Add("portable-app", TagKinds.Software, "Portable app is portable software.");
        Add("setup", TagKinds.Software, "Setup is a software installer.");
        Add("installer", TagKinds.Software, "Installer is software, not a game.");
        Add("exe", TagKinds.Software, "EXE is an executable program.");
        Add("msi", TagKinds.Software, "MSI is a Windows installer package.");
        Add("apk", TagKinds.Software, "APK is an Android application package.");
        Add("deb", TagKinds.Software, "DEB is a Linux software package.");
        Add("rpm", TagKinds.Software, "RPM is a Linux software package.");
        Add("dmg", TagKinds.Software, "DMG is a macOS software image.");
        Add("os", TagKinds.Software, "OS is an operating system.");
        Add("operating-system", TagKinds.Software, "Operating system is system software.");
        Add("linux-distro", TagKinds.Software, "Linux distro is an operating system image.");
        Add("virtual-machine", TagKinds.Software, "Virtual machine is software infrastructure.");
        Add("emulator", TagKinds.Software, "Emulator is software, not a game itself.");
        Add("activator", TagKinds.Software, "Activator is a software licensing tool.");
        Add("preactivated", TagKinds.Software, "Preactivated marks a software license bypass.");
        Add("registry", TagKinds.Software, "Registry is system configuration data.");
        Add("patch", TagKinds.Software, "Patch is a software update; weak/heuristic because games receive patches too.", heuristic: true);
        Add("plugin", TagKinds.Software, "Plugin is a software extension; weak/heuristic because games can be plugins.", heuristic: true);

        Add("dlc", TagKinds.Dlc, "DLC is add-on content, not a standalone game.");
        Add("dlcs", TagKinds.Dlc, "DLCs are add-on content packs.");
        Add("downloadable-content", TagKinds.Dlc, "Downloadable content is add-on content.");
        Add("expansion", TagKinds.Dlc, "Expansion is add-on content.");
        Add("expansion-pack", TagKinds.Dlc, "Expansion pack is add-on content.");
        Add("addon", TagKinds.Dlc, "Addon is add-on content.");
        Add("add-on", TagKinds.Dlc, "Add-on is supplementary content.");
        Add("season-pass", TagKinds.Dlc, "Season pass is add-on content access.");
        Add("preorder-bonus", TagKinds.Dlc, "Preorder bonus is add-on content.");
        Add("bonus", TagKinds.Dlc, "Bonus marks supplementary content.");
        Add("bonus-content", TagKinds.Dlc, "Bonus content is supplementary content.");
        Add("extra-content", TagKinds.Dlc, "Extra content is supplementary content.");
        Add("cosmetics", TagKinds.Dlc, "Cosmetics are add-on content; weak/heuristic.", heuristic: true);

        Add("mod", TagKinds.Mod, "Mod is a user modification, not a game.");
        Add("mods", TagKinds.Mod, "Mods are user modifications.");
        Add("modpack", TagKinds.Mod, "Modpack bundles user modifications.");
        Add("texture-pack", TagKinds.Mod, "Texture pack is a game modification; weak/heuristic.", heuristic: true);
        Add("shaders", TagKinds.Mod, "Shaders are a rendering modification; weak/heuristic.", heuristic: true);
        Add("reshade", TagKinds.Mod, "ReShade is a post-processing modification.");
        Add("enb", TagKinds.Mod, "ENB is a graphics modification.");
        Add("cheat", TagKinds.Mod, "Cheat is a modification tool.");
        Add("cheats", TagKinds.Mod, "Cheats are modification tools.");
        Add("trainer", TagKinds.Mod, "Trainer is a cheat tool; weak/heuristic.", heuristic: true);
        Add("savegame", TagKinds.Mod, "Savegame is saved game data, not a game.");
        Add("save-file", TagKinds.Mod, "Save file is saved game data.");
        Add("save-editor", TagKinds.Mod, "Save editor is a modification tool.");
        Add("custom-map", TagKinds.Mod, "Custom map is user-made content.");
        Add("map-pack", TagKinds.Mod, "Map pack is user-made content.");
        Add("total-conversion", TagKinds.Mod, "Total conversion is a game modification.");

        Add("rom", TagKinds.Rom, "ROM is a game image file, not a catalog game.");
        Add("roms", TagKinds.Rom, "ROMs are game image files.");
        Add("romhack", TagKinds.Rom, "Romhack is a modified ROM image.");
        Add("hackrom", TagKinds.Rom, "Hackrom is a modified ROM image.");
        Add("fan-translation", TagKinds.Rom, "Fan translation patches ROM software.");
        Add("fantranslation", TagKinds.Rom, "Fantranslation patches ROM software.");
        Add("nds", TagKinds.Rom, "NDS is a Nintendo DS ROM platform.");
        Add("gba", TagKinds.Rom, "GBA is a Game Boy Advance ROM platform.");
        Add("gbc", TagKinds.Rom, "GBC is a Game Boy Color ROM platform.");
        Add("snes", TagKinds.Rom, "SNES is a Super Nintendo ROM platform.");
        Add("nes", TagKinds.Rom, "NES is a Nintendo Entertainment System ROM platform.");
        Add("genesis", TagKinds.Rom, "Genesis is a Sega ROM platform.");
        Add("megadrive", TagKinds.Rom, "Mega Drive is a Sega ROM platform.");
        Add("n64", TagKinds.Rom, "N64 is a Nintendo 64 ROM platform.");
        Add("ps1", TagKinds.Rom, "PS1 is a PlayStation ROM platform.");
        Add("ps2", TagKinds.Rom, "PS2 is a PlayStation 2 ROM platform.");
        Add("psx", TagKinds.Rom, "PSX is a PlayStation ROM platform.");
        Add("dreamcast", TagKinds.Rom, "Dreamcast is a Sega ROM platform.");
        Add("saturn", TagKinds.Rom, "Saturn is a Sega ROM platform.");
        Add("gameboy", TagKinds.Rom, "Game Boy is a handheld ROM platform.");
        Add("gb", TagKinds.Rom, "GB is a Game Boy ROM platform.");
        Add("psp", TagKinds.Rom, "PSP is a PlayStation Portable ROM platform.");
        Add("3ds", TagKinds.Rom, "3DS is a Nintendo handheld ROM platform.");
        Add("mame", TagKinds.Rom, "MAME is an arcade ROM emulation platform.");
        Add("arcade-rom", TagKinds.Rom, "Arcade ROM is an arcade game image.");
        Add("cdi", TagKinds.Rom, "CDI is a Dreamcast disc image format.");
        Add("gdi", TagKinds.Rom, "GDI is a Dreamcast disc image format.");
        Add("chd", TagKinds.Rom, "CHD is a compressed disc image format.");
        Add("wbfs", TagKinds.Rom, "WBFS is a Wii disc image format.");
        Add("xci", TagKinds.Rom, "XCI is a Nintendo Switch cartridge image.");
        Add("nsp", TagKinds.Rom, "NSP is a Nintendo Switch package image.");
        Add("cia", TagKinds.Rom, "CIA is a Nintendo 3DS package image.");
        Add("translation-patch", TagKinds.Rom, "Translation patch modifies ROM software; weak/heuristic.", heuristic: true);

        Add("port", TagKinds.Other, "Port marks a platform conversion; weak/heuristic because game ports exist.", heuristic: true);
        Add("repack-proper", TagKinds.Other, "Repack-proper is release-group noise.");
        Add("gog", TagKinds.Other, "GOG is a storefront/installer label, not a game title.");
        Add("fitgirl", TagKinds.Other, "FitGirl is a repack group label.");
        Add("fitgirl-repacks", TagKinds.Other, "FitGirl Repacks is a repack group label.");
        Add("reloaded", TagKinds.Other, "RELOADED is a release-group label.");
        Add("elamigos", TagKinds.Other, "ElAmigos is a repack group label.");
        Add("plaza", TagKinds.Other, "PLAZA is a release-group label.");
        Add("codex", TagKinds.Other, "CODEX is a release-group label.");
        Add("skidrow", TagKinds.Other, "SKIDROW is a release-group label.");
        Add("cpy", TagKinds.Other, "CPY is a release-group label.");
        Add("empress", TagKinds.Other, "EMPRESS is a release-group label.");
        Add("dodi", TagKinds.Other, "DODI is a repack group label.");
        Add("kaos", TagKinds.Other, "KaOs is a repack group label.");
        Add("darck", TagKinds.Other, "DARCK is a repack group label.");
        Add("xatab", TagKinds.Other, "XATAB is a repack group label.");
        Add("rg-mechanics", TagKinds.Other, "RG Mechanics is a repack group label.");
        Add("tinyiso", TagKinds.Other, "TinyISO is a repack label.");
        Add("update", TagKinds.Other, "Update is a patch, not a game; weak/heuristic because games have updates.", heuristic: true);
        Add("updates", TagKinds.Other, "Updates are patches, not games.");
        Add("hotfix", TagKinds.Other, "Hotfix is a patch.");
        Add("demo", TagKinds.Other, "Demo is a trial build; weak/heuristic because demos relate to games.", heuristic: true);
        Add("benchmark", TagKinds.Other, "Benchmark is a performance test tool.");
        Add("benchmarks", TagKinds.Other, "Benchmarks are performance test tools.");
        Add("sdk", TagKinds.Other, "SDK is a software development kit.");
        Add("redistributable", TagKinds.Other, "Redistributable is a runtime installer.");
        Add("redist", TagKinds.Other, "Redist is a runtime installer.");
        Add("vcredist", TagKinds.Other, "VC redist is a runtime installer.");
        Add("directx", TagKinds.Other, "DirectX is a graphics runtime.");
        Add("net-framework", TagKinds.Other, ".NET Framework is a runtime.");
        Add("dotnet", TagKinds.Other, ".NET is a runtime.");
        Add("runtime", TagKinds.Other, "Runtime is a software dependency.");
        Add("crackfix", TagKinds.Other, "Crackfix is a release-group patch.");
        Add("nocd", TagKinds.Other, "No-CD is a software crack.");
        Add("no-cd", TagKinds.Other, "No-CD is a software crack.");
        Add("dlc-unlocker", TagKinds.Other, "DLC unlocker is a modification tool.");
        Add("unlocker", TagKinds.Other, "Unlocker is a modification tool.");
        Add("offline-installer", TagKinds.Other, "Offline installer is a software package.");
        Add("standalone-installer", TagKinds.Other, "Standalone installer is a software package.");
        Add("english-patch", TagKinds.Other, "English patch modifies software language files.");
        Add("beta", TagKinds.Other, "Beta is a pre-release build; weak/heuristic because games have betas.", heuristic: true);
        Add("shareware", TagKinds.Other, "Shareware is a software distribution model.");
        Add("freeware", TagKinds.Other, "Freeware is a software distribution model.");
        Add("abandonware", TagKinds.Other, "Abandonware is unsupported software.");
        Add("compilation", TagKinds.Other, "Compilation is a bundled set; weak/heuristic because game compilations exist.", heuristic: true);
        Add("bundle", TagKinds.Other, "Bundle is a packaged set; weak/heuristic because game bundles exist.", heuristic: true);
        Add("multipack", TagKinds.Other, "Multipack is a packaged set.");
        Add("mod-manager", TagKinds.Other, "Mod manager is a software utility; weak/heuristic.", heuristic: true);

        return list;
    }

    private static HashSet<string> BuildGameTags()
    {
        var tags = new[]
        {
            "rpg", "jrpg", "crpg", "arpg", "action-rpg", "action", "adventure", "platformer", "metroidvania",
            "puzzle", "strategy", "rts", "turn-based", "turn-based-strategy", "tactics", "tactical", "mmo",
            "mmorpg", "shooter", "fps", "tps", "racing", "sports", "simulation", "sim", "horror", "survival",
            "survival-horror", "sandbox", "open-world", "fighting", "beat-em-up", "hack-and-slash", "stealth",
            "rhythm", "card", "deckbuilder", "deck-building", "tower-defense", "moba", "battle-royale",
            "point-and-click", "visual-novel", "dating-sim", "singleplayer", "single-player", "multiplayer",
            "online", "local-co-op", "pvp", "pve", "retro", "pixel", "2d", "3d", "vr", "vr-ready",
            "city-builder", "management", "tycoon", "god-game", "party", "bullet-hell", "shmup", "roguelike",
            "roguelite", "idle", "incremental", "clicker", "crafting", "building", "exploration", "narrative",
            "atmospheric", "story-rich", "early-access", "playtest", "free-to-play", "f2p", "co-op", "coop",
            "indie", "arcade", "action-adventure", "soulslike", "soul-like", "immersive-sim", "boomer-shooter",
            "walking-simulator", "hidden-object", "match-3", "auto-battler", "platform", "metroidvania"
        };
        return new HashSet<string>(tags.Select(Normalize), StringComparer.Ordinal);
    }
}
