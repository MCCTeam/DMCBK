using DMCBK.Core;
using Umpk.Auth;

namespace DMCBK.Samples.ClientGuide;

// This host implements device-code interaction only.
internal sealed class DeviceCodeHost : IHostInterface, IAuthInteraction
{
    public IUserPrompt? Prompt => null;
    public IAuthInteraction AuthInteraction => this;

    public Task ShowDeviceCodeAsync(DeviceCodePrompt prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Console.WriteLine($"Open {prompt.VerificationUri}");
        Console.WriteLine($"Enter code: {prompt.UserCode}");
        Console.WriteLine($"The code expires at {prompt.ExpiresAt:O}.");
        return Task.CompletedTask;
    }

    public Task<string> GetBrowserAuthCodeAsync(Uri signInUrl, CancellationToken ct)
        => throw new NotSupportedException("This sample host supports device-code login only.");

    public Task<YggdrasilCredentials> GetYggdrasilCredentialsAsync(CancellationToken ct)
        => throw new NotSupportedException("This sample host supports device-code login only.");
}
