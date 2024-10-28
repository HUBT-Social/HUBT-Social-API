using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using HUBT_Social_API.Core.Settings;
using HUBT_Social_API.Features.Auth.Dtos.Collections;
using HUBT_Social_API.Features.Auth.Dtos.Reponse;
using HUBT_Social_API.Features.Auth.Dtos.Request;
using HUBT_Social_API.Features.Auth.Models;
using HUBT_Social_API.Features.Auth.Services.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;

namespace HUBT_Social_API.Features.Auth.Services.Child;

public class TokenService : ITokenService
{
    private readonly JwtSetting _jwtSetting;
    private readonly IMongoCollection<RefreshToken> _refreshToken;
    private readonly UserManager<AUser> _userManager;

    public TokenService(
        UserManager<AUser> userManager,
        IOptions<JwtSetting> jwtSettings,
        IMongoCollection<RefreshToken> refreshTokenCollection
    )
    {
        _userManager = userManager;
        _jwtSetting = jwtSettings.Value;
        _refreshToken = refreshTokenCollection;
    }

    public async Task<string> GenerateTokenAsync(AUser user)
    {
        var claims = await GetUserClaimsAsync(user);
        var token = GenerateAccessToken(claims);

        await HandleRefreshTokenAsync(user, token);

        return token;
    }

    public async Task<TokenResponse> RefreshToken(RefreshTokenRequest request)
    {
        var refreshToken = await _refreshToken
            .Find(item => item.RefreshTo == request.RefreshToken)
            .FirstOrDefaultAsync();

        if (refreshToken == null)
        {
            return new TokenResponse 
            { 
                Success = false, 
                Message = "Refresh Token Not Found" 
            };
        }

        var principal = ValidateToken(refreshToken.AccessToken);

        if (!principal.Success)
        {
            return new TokenResponse 
            { 
                Success = false, 
                Message = principal.Message 
            };
        }

        var userId = principal.ClaimsPrincipal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (userId == null)
        {
            return new TokenResponse 
            { 
                Success = false, 
                Message = "Can't find userId in JWT" 
            };
        }

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return new TokenResponse 
            { 
                Success = false, 
                Message = $"Can't find user with Id {userId}" 
            };
        }

        var existingData = await _refreshToken
            .Find(t => t.UserId == userId && t.RefreshTo == request.RefreshToken)
            .FirstOrDefaultAsync();

        if (existingData != null)
        {
            return new TokenResponse
            {
                Token = GenerateAccessToken(await _userManager.GetClaimsAsync(user)),
                Refresh = await ReGenerateToken(user),
                Success = true
            };
        }

        return new TokenResponse 
        { 
            Success = false, 
            Message = "Can't find the owner of this token" 
        };
    }

    public async Task<UserResponse> GetCurrentUser(string accessToken)
    {
        var decodeValue = ValidateToken(accessToken);

        if (!decodeValue.Success)
        {
            return new UserResponse 
            { 
                Success = false, 
                Message = decodeValue.Message 
            };
        }

        var userId = decodeValue.ClaimsPrincipal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userId))
        {
            return new UserResponse 
            { 
                Success = false, 
                Message = "Can't find the owner of this token" 
            };
        }

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return new UserResponse 
            { 
                Success = false, 
                Message = "Can't find the owner of this token" 
            };
        }

        var nameParts = user.FullName.Split(' ');
        
        return new UserResponse
        {
            Email = user.Email,
            StudentCode = user.UserName,
            LastName = nameParts[0],
            FirstName = string.Join(" ", nameParts.Skip(1)),
            Success = true
        };
    }

    public DecodeTokenResponse ValidateToken(string accessToken)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var tokenKey = Encoding.UTF8.GetBytes(_jwtSetting.SecretKey);

        try
        {
            var principal = tokenHandler.ValidateToken(accessToken, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = false,
                IssuerSigningKey = new SymmetricSecurityKey(tokenKey)
            }, out var securityToken);

            if (securityToken is JwtSecurityToken token &&
                token.Header.Alg.Equals(SecurityAlgorithms.HmacSha256))
            {
                if (token.ValidTo < DateTime.UtcNow)
                {
                    return new DecodeTokenResponse 
                    { 
                        Success = false, 
                        Message = "Token is expired" 
                    };
                }

                return new DecodeTokenResponse 
                { 
                    Success = true, 
                    ClaimsPrincipal = principal 
                };
            }

            return new DecodeTokenResponse 
            { 
                Success = false, 
                Message = "Token algorithm mismatch" 
            };
        }
        catch (Exception ex)
        {
            return new DecodeTokenResponse 
            { 
                Success = false, 
                Message = ex.Message 
            };
        }
    }

    private async Task<string> ReGenerateToken(AUser user)
    {
        var refreshToken = GenerateRefreshToken(await GetUserClaimsAsync(user));
        var existingToken = await _refreshToken.Find(t => t.UserId == user.Id.ToString()).FirstOrDefaultAsync();

        if (existingToken != null)
        {
            var update = Builders<RefreshToken>.Update.Set(t => t.RefreshTo, refreshToken);
            await _refreshToken.UpdateOneAsync(t => t.UserId == user.Id.ToString(), update);
        }
        else
        {
            await _refreshToken.InsertOneAsync(new RefreshToken
            {
                UserId = user.Id.ToString(),
                AccessToken = GenerateAccessToken(await GetUserClaimsAsync(user)),
                RefreshTo = refreshToken
            });
        }

        return refreshToken;
    }

    private async Task HandleRefreshTokenAsync(AUser user, string accessToken)
    {
        var existingRefreshToken = await _refreshToken.Find(t => t.UserId == user.Id.ToString()).FirstOrDefaultAsync();

        if (existingRefreshToken == null)
        {
            await _refreshToken.InsertOneAsync(new RefreshToken
            {
                AccessToken = accessToken,
                UserId = user.Id.ToString()
            });
        }
        else
        {
            var update = Builders<RefreshToken>.Update.Set(t => t.AccessToken, accessToken);
            await _refreshToken.UpdateOneAsync(t => t.UserId == existingRefreshToken.UserId, update);
        }
    }

    private async Task<List<Claim>> GetUserClaimsAsync(AUser user)
    {
        var claims = new List<Claim>();
        claims.AddRange(await _userManager.GetClaimsAsync(user));

        var roles = await _userManager.GetRolesAsync(user);
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        return claims;
    }

    private string GenerateToken(IEnumerable<Claim> claims, string secretKey, Func<DateTime> expiration)
    {
        if (string.IsNullOrEmpty(secretKey))
            throw new ArgumentException("SecretKey must not be null or empty.");

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = expiration(),
            SigningCredentials = credentials
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(tokenDescriptor);

        return tokenHandler.WriteToken(token);
    }

    private string GenerateAccessToken(IEnumerable<Claim> claims)
    {
        return GenerateToken(
            claims,
            _jwtSetting.SecretKey,
            () => DateTime.UtcNow.AddMinutes(_jwtSetting.TokenExpirationInMinutes)
        );
    }

    private string GenerateRefreshToken(IEnumerable<Claim> claims)
    {
        return GenerateToken(
            claims,
            _jwtSetting.RefreshSecretKey,
            () => DateTime.UtcNow.AddDays(_jwtSetting.RefreshTokenExpirationInDays)
        );
    }
}
