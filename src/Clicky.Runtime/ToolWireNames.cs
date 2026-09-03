using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Clicky.Core;

namespace Clicky.Runtime;

public static class ToolWireNames
{
    public static string Encode(string internalName)
    {
        if (Regex.IsMatch(internalName, "^[a-zA-Z0-9_-]{1,64}$"))
            return internalName;
        var prefix = Regex.Replace(internalName, "[^a-zA-Z0-9_-]", "_");
        if (prefix.Length > 47)
            prefix = prefix[..47];
        return prefix + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(internalName)))[..12].ToLowerInvariant();
    }
    public static string Decode(string wireName, ModelRequest request)
    {
        var known = (request.Tools?.Select(t => t.Name) ?? []).Concat(request.Messages.SelectMany(m => m.ToolCalls ?? []).Select(c => c.Name));
        return known.FirstOrDefault(name => Encode(name) == wireName) ?? wireName;
    }
}
