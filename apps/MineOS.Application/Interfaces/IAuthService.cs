using MineOS.Application.Dtos;

namespace MineOS.Application.Interfaces;

public interface IAuthService
{
    Task<LoginResultDto?> LoginAsync(LoginRequestDto request, CancellationToken cancellationToken);

    /// <summary>
    /// Issues a fresh token for an already-authenticated user, so an active
    /// session does not end when its token expires. Returns null when the user
    /// no longer exists or has been deactivated.
    /// </summary>
    Task<LoginResultDto?> RefreshAsync(int userId, CancellationToken cancellationToken);
}
