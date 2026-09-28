using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace GameLibrary.Native;

internal static class GameIdentityTests
{
    internal static void Run(string root)
    {
        string fixture = Path.Combine(root, "canonical-identity-fixture");
        Directory.CreateDirectory(fixture);
        string executable = Path.Combine(fixture, "Game.exe");
        File.WriteAllText(executable, "fixture");
        var games = new[]
        {
            new Game { Id = "docker:older", Name = "Custom installed name", DockerImage = "repo:older", Rating = 4, Installed = true, PlayedHours = 3 },
            new Game { Id = "docker:newer", Name = "Shared Title (published)", DockerImage = "repo:newer", Image = "cover-newer.jpg", Wishlisted = true },
            new Game { Id = "local:game", Name = "Shared Title", IsLocal = true, MetadataSteamAppId = 1234 },
            new Game { Id = "wand:game", Name = "Different Alias", IsLocal = true, MetadataSteamAppId = 1234 },
            new Game { Id = "unrelated", Name = "Shared Title", DockerImage = "other:latest" }
        };
        var state = new UserState();
        state.LaunchPaths["docker:older"] = executable;
        state.LaunchPaths["local:game"] = executable;
        state.LaunchPaths["wand:game"] = executable;
        games[3].CanPlayWithWand = true;
        state.InstalledGames.Add("docker:older");
        state.LastPlayedUtc["docker:older"] = DateTime.UtcNow;
        var latestLinkedPlay = DateTime.UtcNow.AddMinutes(1);
        games[2].LastPlayedUtc = latestLinkedPlay;
        state.GameTags["docker:older"] = new List<string> { "personal tag" };
        state.GameTags["docker:newer"] = new List<string> { "shared tag" };
        state.Wishlist.Add("docker:newer");
        var digest = "sha256:" + new string('a', 64);
        var digests = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["docker:older"] = digest, ["docker:newer"] = digest
        };
        var index = new GameIdentityIndex(games, state, digests);
        if (index.Groups().Count != 2 || index.CanonicalId("docker:older") != index.CanonicalId("wand:game")
            || index.CanonicalId("unrelated") == index.CanonicalId("docker:older"))
            throw new InvalidOperationException("Verified source relationships were not grouped conservatively.");
        var pushed = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
        {
            ["docker:older"] = DateTimeOffset.UtcNow.AddDays(-2),
            ["docker:newer"] = DateTimeOffset.UtcNow.AddDays(-1),
            ["unrelated"] = DateTimeOffset.UtcNow
        };
        var projected = GameCardProjection.Select(games, state, index, pushed);
        var card = projected.Single(x => x.SourceIds.Contains("docker:older"));
        if (card.PublishedRepresentativeId != "docker:newer" || card.PlayableInstallationId != "docker:older"
            || !card.PublicationFreshnessVerified || !card.SourceIds.Contains("local:game"))
            throw new InvalidOperationException("Published version displaced or lost the existing installation.");
        pushed.Remove("docker:newer");
        var stale = GameCardProjection.Select(games, state, index, pushed,
            new Dictionary<string, string>(StringComparer.Ordinal) { [card.CanonicalId] = "docker:older" })
            .Single(x => x.SourceIds.Contains("docker:older"));
        if (stale.PublicationFreshnessVerified || stale.PublishedRepresentativeId != "docker:older")
            throw new InvalidOperationException("Missing push evidence changed the last verified representative.");
        var staleCard = GameCardProjection.ProjectCards(games, state, index, pushed,
                new Dictionary<string, string>(StringComparer.Ordinal) { [card.CanonicalId] = "docker:older" })
            .Single(x => x.SourceRecords.Count > 1);
        if (staleCard.Id != "docker:older" || staleCard.PublicationFreshnessVerified
            || !staleCard.Meta.Contains("Published image candidate: repo:older (freshness unverified)", StringComparison.Ordinal)
            || staleCard.Meta.Contains("Latest published image:", StringComparison.Ordinal))
            throw new InvalidOperationException("An unverified published representative was presented as the latest version.");
        GameCardProjection.Detach(staleCard);

        pushed["docker:newer"] = DateTimeOffset.UtcNow.AddDays(-1);
        var cardFactory = typeof(GameCardProjection).GetMethod("ProjectCards",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        if (cardFactory == null)
            throw new InvalidOperationException("Verified identity groups are not projected into library cards.");
        var cards = cardFactory.Invoke(null, new object?[] { games, state, index, pushed, new Dictionary<string, string>(StringComparer.Ordinal) })
            as IReadOnlyList<Game> ?? throw new InvalidOperationException("Card projection returned an unsupported result.");
        var groupedCard = cards.Single(card => card.Id == "docker:older");
        if (GameCardProjection.SelectWandSource(groupedCard, state, new HashSet<string>(StringComparer.Ordinal) { "wand:game" }) != games[3])
            throw new InvalidOperationException("A grouped card did not route Wand to its exact registered source.");
        var sourceRecords = typeof(Game).GetProperty("SourceRecords", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var actionTarget = typeof(Game).GetProperty("ActionTarget", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var publishedRepresentative = typeof(Game).GetProperty("PublishedRepresentative", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var freshness = typeof(Game).GetProperty("PublicationFreshnessVerified", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var sources = sourceRecords?.GetValue(groupedCard) as IReadOnlyList<Game>;
        if (cards.Count != 2 || sources?.Count != 4 || actionTarget?.GetValue(groupedCard) != games[0]
            || publishedRepresentative?.GetValue(groupedCard) != games[1] || freshness?.GetValue(groupedCard) is not true
            || groupedCard.DockerImage != "repo:newer" || groupedCard.Image != "cover-newer.jpg"
            || groupedCard.Name != "Custom installed name"
            || groupedCard.Rating != 4 || !groupedCard.Wishlisted || groupedCard.TagsLabel != "personal tag  ·  shared tag"
            || groupedCard.LastPlayedUtc != latestLinkedPlay || !groupedCard.Installed || groupedCard.PlayedHours != 3
            || !groupedCard.CanPlayWithWand || games[0].CanPlayWithWand
            || !groupedCard.Meta.Contains("Latest published image: repo:newer", StringComparison.Ordinal)
            || !groupedCard.Meta.Contains("Published title: Shared Title (published)", StringComparison.Ordinal)
            || !groupedCard.Meta.Contains("Play/save target remains docker:older", StringComparison.Ordinal)
            || games[0].Name != "Custom installed name" || games[0].DockerImage != "repo:older")
            throw new InvalidOperationException("Grouped card lost its installed action target, published metadata, personal fields, or source records.");
        games[1].Name = "Updated published title"; games[1].Notify(nameof(Game.Name));
        games[0].Rating = 2; games[0].Notify(nameof(Game.Rating));
        if (groupedCard.Name != "Custom installed name" || !groupedCard.Meta.Contains("Published title: Updated published title", StringComparison.Ordinal) || groupedCard.Rating != 2)
            throw new InvalidOperationException("Grouped card did not follow updates from its source records.");
        games[3].CanPlayWithWand = false; games[3].Notify(nameof(Game.CanPlayWithWand));
        if (groupedCard.CanPlayWithWand)
            throw new InvalidOperationException("Grouped Wand eligibility did not follow the exact registered source.");
        GameCardProjection.Detach(cards);

        string deltaPath = Path.Combine(fixture, "deltarune-856b73d0", "DELTARUNE.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(deltaPath)!);
        File.WriteAllText(deltaPath, "fixture");
        var delta = new Game { Id = "deltarune", Name = "DELTARUNE" };
        var deltaRegistration = new WandSupportedGame("deltarune-856b73d0", "delta-title", "57393", "DELTARUNE", deltaPath);
        var deltaState = new UserState();
        deltaState.LaunchPaths[delta.Id] = deltaPath;
        if (MainWindow.ResolveWandRegistrationMatch(new[] { delta }, deltaState, deltaRegistration) != delta)
            throw new InvalidOperationException("The exact DELTARUNE Wand path did not resolve to its saved game record.");

        string ashenPath = Path.Combine(fixture, "Ashen", "Ashen-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(ashenPath)!);
        File.WriteAllText(ashenPath, "fixture");
        var opaqueAlias = new Game { Id = "Win64", Name = "Win64" };
        var titleMatch = new Game { Id = "Ashen", Name = "Ashen" };
        var savedWandIdentity = new Game { Id = "wand:27643", Name = "Ashen" };
        var ashenRegistration = new WandSupportedGame("Ashen", "ashen-title", "27643", "Ashen", ashenPath);
        var ashenState = new UserState();
        ashenState.LaunchPaths[opaqueAlias.Id] = ashenPath;
        ashenState.LaunchPaths[titleMatch.Id] = ashenPath;
        ashenState.LaunchPaths[savedWandIdentity.Id] = ashenPath;
        if (MainWindow.ResolveWandRegistrationMatch(new[] { opaqueAlias, titleMatch }, ashenState, ashenRegistration) != titleMatch
            || MainWindow.ResolveWandRegistrationMatch(new[] { opaqueAlias, titleMatch, savedWandIdentity }, ashenState, ashenRegistration) != savedWandIdentity)
            throw new InvalidOperationException("An exact Wand registration did not prefer its stable Wand identity and title-matched game over a folder alias.");

        var ambiguousA = new Game { Id = "candidate-a", Name = "Ashen" };
        var ambiguousB = new Game { Id = "candidate-b", Name = "Ashen" };
        ashenState.LaunchPaths[ambiguousA.Id] = ashenPath;
        ashenState.LaunchPaths[ambiguousB.Id] = ashenPath;
        var ambiguousRegistration = ashenRegistration with { Folder = "unmatched-folder" };
        if (MainWindow.ResolveWandRegistrationMatch(new[] { ambiguousA, ambiguousB }, ashenState, ambiguousRegistration) != null)
            throw new InvalidOperationException("An ambiguous exact Wand path was assigned to an arbitrary catalog record.");

        var registrationSet = new[]
        {
            new WandSupportedGame("Ashen", "ashen-title", "27643", "Ashen", @"E:\games\Ashen.exe"),
            new WandSupportedGame("DELTARUNE", "delta-title", "57393", "DELTARUNE", @"E:\games\DELTARUNE.exe")
        };
        var reorderedSet = registrationSet.Reverse().ToArray();
        var pathCaseOnly = registrationSet.Select(row => row with { Path = row.Path.ToLowerInvariant() }).ToArray();
        var addedRegistration = registrationSet.Append(new WandSupportedGame("Other", "other-title", "1", "Other", @"E:\games\Other.exe")).ToArray();
        var movedRegistration = registrationSet.Select(row => row.GameId == "57393" ? row with { Path = @"E:\other\DELTARUNE.exe" } : row).ToArray();
        var emptyRegistrationSet = Array.Empty<WandSupportedGame>();
        if (!WandLiveLibrary.SameRegistrationSet(registrationSet, reorderedSet)
            || !WandLiveLibrary.SameRegistrationSet(registrationSet, pathCaseOnly)
            || WandLiveLibrary.SameRegistrationSet(registrationSet, addedRegistration)
            || WandLiveLibrary.SameRegistrationSet(registrationSet, movedRegistration)
            || WandLiveLibrary.SameRegistrationSet(registrationSet, emptyRegistrationSet))
            throw new InvalidOperationException("Wand registration change detection did not distinguish membership/path changes from order or Windows path casing.");
        using var registrationNotification = new ManualResetEventSlim();
        int registrationNotificationCount = 0;
        EventHandler registrationHandler = (_, _) =>
        {
            Interlocked.Increment(ref registrationNotificationCount);
            registrationNotification.Set();
        };
        WandLiveLibrary.RegistrationSetChanged += registrationHandler;
        try
        {
            if (!WandLiveLibrary.NotifyRegistrationSetChangedIfNeeded(registrationSet, addedRegistration, initialSnapshot: false)
                || !registrationNotification.Wait(TimeSpan.FromSeconds(2))
                || WandLiveLibrary.NotifyRegistrationSetChangedIfNeeded(registrationSet, reorderedSet, initialSnapshot: false)
                || WandLiveLibrary.NotifyRegistrationSetChangedIfNeeded(registrationSet, addedRegistration, initialSnapshot: true))
                throw new InvalidOperationException("Wand did not notify a changed registration set exactly when a prior snapshot existed.");
            Thread.Sleep(50);
            if (Volatile.Read(ref registrationNotificationCount) != 1)
                throw new InvalidOperationException("Wand emitted more than one notification for the changed registration set.");
            registrationNotification.Reset();
            if (!WandLiveLibrary.NotifyRegistrationSetChangedIfNeeded(addedRegistration, emptyRegistrationSet, initialSnapshot: false)
                || !registrationNotification.Wait(TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException("Wand did not notify when the last registrations were removed.");
            Thread.Sleep(50);
            if (Volatile.Read(ref registrationNotificationCount) != 2)
                throw new InvalidOperationException("Wand removal notification did not fire exactly once.");
        }
        finally { WandLiveLibrary.RegistrationSetChanged -= registrationHandler; }
    }
}
