using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using PayX.Identity.Domain;
using PayX.Identity.Services;

namespace PayX.Identity.Api;

public sealed record RegisterUserRequest(
    [Required, RegularExpression("^[A-Za-z0-9_]{3,32}$",
        ErrorMessage = "3-32 letters, digits or underscores")] string Username,
    [Required, EmailAddress, MaxLength(254)] string Email,
    // Length over complexity rules (NIST SP 800-63B): a long passphrase beats
    // "P@ssw0rd!". The upper bound stops a megabyte "password" from costing
    // 100,000 hash iterations' worth of CPU per request.
    [Required, MinLength(12), MaxLength(128)] string Password,
    [Required] UserRole Role);

// What a client gets back. Never the password hash, not even hashed.
public sealed record UserResponse(Guid Id, string Username, string Email, UserRole Role, DateTimeOffset CreatedAt);

// No format rules here, unlike registration: a login must not reveal which
// usernames or passwords *could* exist, only "right" or "wrong".
public sealed record LoginRequest([Required, MaxLength(32)] string Username, [Required, MaxLength(128)] string Password);

public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn);

[ApiController]
[Route("api/users")]
public sealed class UsersController(UserRegistration registration, UserAuthentication authentication) : ControllerBase
{
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var token = await authentication.AuthenticateAsync(request.Username, request.Password, ct);

        // One answer for "no such user" and "wrong password", so a login
        // attempt can't be used to discover which usernames exist.
        return token is null
            ? Problem(statusCode: 401, title: "Invalid username or password")
            : Ok(new TokenResponse(token.AccessToken, "Bearer", token.ExpiresInSeconds));
    }

    // [ApiController] has already turned any failed validation attribute
    // above into a 400 ProblemDetails before this method runs.
    [HttpPost]
    public async Task<IActionResult> Register(RegisterUserRequest request, CancellationToken ct)
    {
        var (outcome, user) = await registration.RegisterAsync(
            request.Username, request.Email, request.Password, request.Role, ct);

        return outcome switch
        {
            RegistrationOutcome.Created => Created($"/api/users/{user!.Id}",
                new UserResponse(user.Id, user.Username, user.Email, user.Role, user.CreatedAt)),
            RegistrationOutcome.UsernameTaken => Problem(statusCode: 409, title: "Username already taken"),
            RegistrationOutcome.EmailTaken => Problem(statusCode: 409, title: "Email already registered"),
            RegistrationOutcome.RoleNotAllowed => Problem(statusCode: 400, title: "Only Customer or Merchant accounts can be registered"),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome}"),
        };
    }
}
