namespace Clicky.Core;

public sealed class KnowledgeStore
{
    private readonly string memory;
    private readonly string skills;
    public KnowledgeStore(string? directory = null)
    {
        var root = directory ?? AppPaths.Root;
        memory = Path.Combine(root, "Memory");
        skills = Path.Combine(root, "Skills");
        Directory.CreateDirectory(memory);
        Directory.CreateDirectory(skills);
        if (!File.Exists(Path.Combine(memory, "PROFILE.md")))
            File.WriteAllText(Path.Combine(memory, "PROFILE.md"), "# My profile\n\nAdd preferences you want HeyBuddy to remember. Stored locally and included in your selected provider context.\n");
    }
    public string ReadProfile() => File.ReadAllText(Path.Combine(memory, "PROFILE.md"));
    public void SaveProfile(string text) => AtomicWrite(Path.Combine(memory, "PROFILE.md"), text);
    public IReadOnlyList<SkillDocument> GetSkills() => Directory.EnumerateFiles(skills, "*.md").OrderBy(Path.GetFileName).Select(path => new SkillDocument(Path.GetFileNameWithoutExtension(path).Replace(".disabled", "", StringComparison.Ordinal), File.ReadAllText(path), !path.EndsWith(".disabled.md", StringComparison.OrdinalIgnoreCase), path)).ToArray();
    public void SaveSkill(string name, string content, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Use a simple skill name without path characters.");
        var path = Path.Combine(skills, name + (enabled ? ".md" : ".disabled.md"));
        var other = Path.Combine(skills, name + (enabled ? ".disabled.md" : ".md"));
        AtomicWrite(path, content);
        if (File.Exists(other))
        {
            File.Copy(other, other + ".bak", true);
            File.Delete(other);
        }
    }
    public string Context()
    {
        var context = ReadProfile() + "\n\n" + string.Join("\n\n", GetSkills().Where(s => s.Enabled).Select(s => $"Skill: {s.Name}\n{s.Content}"));
        return context.Length > 24000 ? context[..24000] : context;
    }
    private static void AtomicWrite(string path, string content)
    {
        if (content.Length > 100000)
            throw new ArgumentException("Keep each memory or skill file under 100,000 characters.");
        File.WriteAllText(path + ".tmp", content);
        if (File.Exists(path))
            File.Copy(path, path + ".bak", true);
        File.Move(path + ".tmp", path, true);
    }
}
