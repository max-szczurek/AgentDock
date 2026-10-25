using AgentDock.Core.Services;
using AgentDock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentDock.Web.Pages.Skills;

public class IndexModel(ISkillService skills, ICurrentAccountAccessor accounts) : PageModel
{
    public IReadOnlyList<SkillSummary> Items { get; private set; } = [];
    public IReadOnlyList<SkillUploadResult> Results { get; private set; } = [];
    [TempData] public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        Items = await skills.ListAsync(account.Id, ct);
        return Page();
    }

    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> OnPostUploadAsync(List<IFormFile> files, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        var uploads = new List<SkillUpload>();
        foreach (var f in files.Take(50))
        {
            await using var s = f.OpenReadStream();
            using var ms = new MemoryStream();
            // Read one byte past the limit so oversize files are rejected without buffering them whole.
            await s.CopyToAsync(new LimitedStream(ms, SkillRules.MaxFileBytes + 1), ct);
            uploads.Add(new SkillUpload(f.FileName, ms.ToArray()));
        }
        Results = uploads.Count == 0 ? [new SkillUploadResult("(none)", false, "Choose at least one .md file.", null)] : await skills.UploadAsync(account.Id, uploads, ct);
        Items = await skills.ListAsync(account.Id, ct);
        return Page();
    }

    private sealed class LimitedStream(Stream inner, int limit) : Stream
    {
        private int _written;
        public override void Write(byte[] buffer, int offset, int count)
        {
            var take = Math.Min(count, limit - _written);
            if (take > 0) { inner.Write(buffer, offset, take); _written += take; }
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var take = Math.Min(buffer.Length, limit - _written);
            if (take > 0) { inner.Write(buffer[..take]); _written += take; }
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) { Write(buffer, offset, count); return Task.CompletedTask; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Write(buffer.Span); return ValueTask.CompletedTask; }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
