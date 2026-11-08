namespace MineOS.Application.Dtos;

// ---- bulk delete -----------------------------------------------------------

public record ModOperationFailureDto(string FileName, string Error);

public record ModDeleteBatchResultDto(IReadOnlyList<string> Deleted, IReadOnlyList<ModOperationFailureDto> Failed);

// ---- compatibility check ---------------------------------------------------

/// <summary>What a mod jar says about itself.</summary>
/// <param name="Platform">fabric, quilt, forge or neoforge; null when the jar has no loader metadata.</param>
/// <param name="Environment">both, client or server.</param>
public record ModInfoDto(
    string FileName,
    bool IsDisabled,
    string? ModId,
    string? Name,
    string? Version,
    string? Platform,
    string Environment);

/// <param name="Severity">error (the server will likely not start) or warning.</param>
/// <param name="Kind">client-only, missing-dependency, duplicate or wrong-loader.</param>
public record ModIssueDto(string Severity, string Kind, string Message, IReadOnlyList<string> FileNames);

public record ModCheckResultDto(
    string? ServerLoader,
    string? MinecraftVersion,
    IReadOnlyList<ModInfoDto> Mods,
    IReadOnlyList<ModIssueDto> Issues);

// ---- updates ---------------------------------------------------------------

public record ModUpdateDto(
    string FileName,
    string ProjectId,
    string CurrentVersion,
    string LatestVersionId,
    string LatestVersion,
    string LatestFileName,
    DateTimeOffset Published);

/// <param name="ModsChecked">Enabled jars that were hashed.</param>
/// <param name="RecognizedOnModrinth">How many of them Modrinth knows; the rest cannot be checked.</param>
public record ModUpdateCheckDto(
    string? Loader,
    string? MinecraftVersion,
    int ModsChecked,
    int RecognizedOnModrinth,
    IReadOnlyList<ModUpdateDto> Updates);

public record ModUpdateResultDto(string OldFileName, string NewFileName, string Version);
