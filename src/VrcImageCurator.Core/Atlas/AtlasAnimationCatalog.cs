using VrcImageCurator.Core.Models;
using VrcImageCurator.Core.Storage;

namespace VrcImageCurator.Core.Atlas;

/// <summary>One archived sheet and the animation that belongs to it.</summary>
public sealed record ArchivedSheet(
    Guid Id,
    VrcImageCategory Category,
    string AtlasPath,
    string ArchiveRoot,
    string AnimationPath,
    bool HasAnimation,
    EmojiAtlasName Name,
    string Fingerprint = "",
    bool IsSkipped = false)
{
    public string FileName => System.IO.Path.GetFileName(AtlasPath);

    public string Summary =>
        $"{Name.FrameCount} frames · {Name.FramesPerSecond} fps · "
        + (Name.LoopStyle == AtlasLoopStyle.PingPong ? "ping-pong" : "linear")
        + (HasAnimation ? string.Empty : IsSkipped ? " · skipped" : " · not exported");

    /// <summary>Still waiting on a decision: no animation, and not one a person has skipped.</summary>
    public bool NeedsDecision => !HasAnimation && !IsSkipped;

    /// <summary>
    /// The rate the exported file will really play at. GIF cannot express every rate, so most land
    /// on the nearest one a viewer will honour.
    /// </summary>
    public int EffectiveFramesPerSecond =>
        AtlasGifExporter.EffectiveFramesPerSecondFor(Name.FramesPerSecond);

    /// <summary>
    /// True only when the rate was too fast for GIF to express and had to be pulled down, rather
    /// than merely landing on a neighbouring whole hundredth.
    /// </summary>
    /// <remarks>
    /// This used to compare a truncated quotient against the requested rate, which called almost
    /// every real sheet clamped - 31, 30, 24 and 15 fps all reported it - because nearly no rate
    /// divides 100 exactly. Only the ceiling is a real clamp.
    /// </remarks>
    public bool RateWasClamped =>
        Name.FramesPerSecond > AtlasGifExporter.MaximumRepresentableFramesPerSecond;
}

/// <summary>
/// Lists the animated emoji in the archive and re-exports them on request.
/// </summary>
/// <remarks>
/// Everything here is derived from the file name and the archive index, so the list costs one
/// file-exists check per archived sheet and nothing is decoded until an export is asked for.
/// </remarks>
public sealed class AtlasAnimationCatalog
{
    private readonly JsonStateStore _stateStore;
    private readonly AtlasAnimationWriter _writer = new();

    public AtlasAnimationCatalog(JsonStateStore stateStore)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    public async Task<IReadOnlyList<ArchivedSheet>> ListAsync(CancellationToken cancellationToken = default)
    {
        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var skipped = new HashSet<string>(state.SkippedAnimations, StringComparer.OrdinalIgnoreCase);
        var roots = state.Settings.CategoryMappings
            .GroupBy(mapping => mapping.Category)
            .ToDictionary(group => group.Key, group => group.First().ArchivePath);

        var sheets = new List<ArchivedSheet>();
        foreach (var category in state.ArchiveIndex.Categories)
        {
            var root = roots.TryGetValue(category.Category, out var mapped) ? mapped : string.Empty;
            foreach (var image in category.Images)
            {
                if (!EmojiAtlasName.TryParse(image.Path, out var name))
                {
                    continue;
                }

                string destination;
                bool exists;
                try
                {
                    destination = AtlasAnimationWriter.BuildDestination(image.Path, root);
                    exists = File.Exists(destination);
                }
                catch (Exception exception) when (
                    exception is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                sheets.Add(new ArchivedSheet(
                    image.Id,
                    category.Category,
                    image.Path,
                    root,
                    destination,
                    exists,
                    name,
                    image.ExactFingerprint,
                    skipped.Contains(image.ExactFingerprint)));
            }
        }

        return sheets
            .OrderBy(sheet => sheet.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Exports one sheet using <paramref name="name"/>, which may differ from the one in the file
    /// name when the name is wrong and you are correcting it by hand.
    /// </summary>
    public Task<AtlasAnimationResult> ExportAsync(
        ArchivedSheet sheet,
        EmojiAtlasName name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(name);
        return _writer.TryWriteAsync(sheet.AtlasPath, sheet.ArchiveRoot, name, cancellationToken);
    }

    /// <summary>
    /// Marks sheets as skipped, so they stop counting as work still to do. Nothing on disk moves
    /// or is deleted: a skip is a note that this sheet is not worth animating.
    /// </summary>
    public Task<int> SkipAsync(
        IEnumerable<ArchivedSheet> sheets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        var keys = sheets
            .Select(sheet => sheet.Fingerprint)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToArray();

        return UpdateSkipsAsync(
            skips =>
            {
                var added = 0;
                foreach (var key in keys)
                {
                    if (skips.Add(key))
                    {
                        added++;
                    }
                }

                return added;
            },
            cancellationToken);
    }

    /// <summary>Puts skipped sheets back in the queue.</summary>
    public Task<int> RestoreAsync(
        IEnumerable<ArchivedSheet> sheets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        var keys = sheets.Select(sheet => sheet.Fingerprint).ToArray();
        return UpdateSkipsAsync(
            skips => keys.Count(key => skips.Remove(key)),
            cancellationToken);
    }

    private async Task<int> UpdateSkipsAsync(
        Func<HashSet<string>, int> change,
        CancellationToken cancellationToken)
    {
        var changed = 0;
        await _stateStore
            .UpdateAsync(
                state =>
                {
                    var skips = new HashSet<string>(state.SkippedAnimations, StringComparer.OrdinalIgnoreCase);
                    changed = change(skips);
                    if (changed == 0)
                    {
                        return false;
                    }

                    state.SkippedAnimations = skips.Order(StringComparer.OrdinalIgnoreCase).ToList();
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
        return changed;
    }
}
