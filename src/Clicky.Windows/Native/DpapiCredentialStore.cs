using System.Security.Cryptography;
using System.IO;
using System.Text;
using Clicky.Core;

namespace Clicky.Windows.Native;

public sealed class DpapiCredentialStore : ICredentialStore
{
    private readonly string directory;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClickyLocal.Credentials.v1");
    public DpapiCredentialStore(string? directory = null)
    {
        this.directory = directory ?? Path.Combine(AppPaths.Root, "Credentials");
    }
    private string FileName(string name) => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))) + ".bin");
    public string? Get(string name)
    {
        var path = FileName(name);
        if (!File.Exists(path))
            return null;
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Set(string name, string value)
    {
        Directory.CreateDirectory(directory);
        var plain = Encoding.UTF8.GetBytes(value);
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var path = FileName(name);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, path, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Delete(string name)
    {
        var path = FileName(name);
        if (File.Exists(path))
            File.Delete(path);
    }
}
