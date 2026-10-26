using System.Net;
using MX.Domain;
using MX.MailServer.Authentication;

namespace MX.Tests.MailServer;

public sealed class SpfTests
{
    private static Task<SpfOutcome> Check(FakeDns dns, string ip, string sender = "user@sender.test") =>
        new SpfEvaluator(dns).CheckAsync(IPAddress.Parse(ip), sender, "helo.sender.test", CancellationToken.None);

    [Theory]
    [InlineData("v=spf1 ip4:192.0.2.0/24 -all", "192.0.2.77", AuthCheckResult.Pass)]
    [InlineData("v=spf1 ip4:192.0.2.0/24 -all", "198.51.100.1", AuthCheckResult.Fail)]
    [InlineData("v=spf1 ip4:192.0.2.0/24 ~all", "198.51.100.1", AuthCheckResult.SoftFail)]
    [InlineData("v=spf1 ?all", "198.51.100.1", AuthCheckResult.Neutral)]
    [InlineData("v=spf1 ip6:2001:db8::/32 -all", "2001:db8::25", AuthCheckResult.Pass)]
    [InlineData("v=spf1 ip4:192.0.2.1", "198.51.100.1", AuthCheckResult.Neutral)]
    [InlineData("v=spf1 foo:bar -all", "192.0.2.1", AuthCheckResult.PermError)]
    public async Task Evaluates_basic_mechanisms(string record, string ip, AuthCheckResult expected)
    {
        var dns = new FakeDns().AddTxt("sender.test", record);
        Assert.Equal(expected, (await Check(dns, ip)).Result);
    }

    [Fact]
    public async Task No_record_is_none_and_multiple_records_is_permerror()
    {
        Assert.Equal(AuthCheckResult.None, (await Check(new FakeDns().AddTxt("sender.test", "some other txt"), "192.0.2.1")).Result);
        var dns = new FakeDns().AddTxt("sender.test", "v=spf1 -all").AddTxt("sender.test", "v=spf1 +all");
        Assert.Equal(AuthCheckResult.PermError, (await Check(dns, "192.0.2.1")).Result);
    }

    [Fact]
    public async Task Follows_a_mx_and_include()
    {
        var dns = new FakeDns()
            .AddTxt("sender.test", "v=spf1 a mx include:_spf.provider.test -all")
            .AddA("sender.test", "192.0.2.10")
            .AddMx("sender.test", "mx1.sender.test").AddA("mx1.sender.test", "192.0.2.20")
            .AddTxt("_spf.provider.test", "v=spf1 ip4:203.0.113.0/24 -all");
        Assert.Equal(AuthCheckResult.Pass, (await Check(dns, "192.0.2.10")).Result);
        Assert.Equal(AuthCheckResult.Pass, (await Check(dns, "192.0.2.20")).Result);
        Assert.Equal(AuthCheckResult.Pass, (await Check(dns, "203.0.113.9")).Result);
        Assert.Equal(AuthCheckResult.Fail, (await Check(dns, "198.51.100.9")).Result);
    }

    [Fact]
    public async Task A_mechanism_honours_cidr()
    {
        var dns = new FakeDns().AddTxt("sender.test", "v=spf1 a:web.sender.test/24 -all").AddA("web.sender.test", "192.0.2.10");
        Assert.Equal(AuthCheckResult.Pass, (await Check(dns, "192.0.2.200")).Result);
    }

    [Fact]
    public async Task Redirect_is_followed()
    {
        var dns = new FakeDns().AddTxt("sender.test", "v=spf1 redirect=_spf.sender.test").AddTxt("_spf.sender.test", "v=spf1 ip4:192.0.2.5 -all");
        Assert.Equal(AuthCheckResult.Pass, (await Check(dns, "192.0.2.5")).Result);
    }

    [Fact]
    public async Task More_than_ten_lookups_is_permerror()
    {
        var dns = new FakeDns();
        for (var i = 0; i < 12; i++) dns.AddTxt(i == 0 ? "sender.test" : $"l{i}.test", $"v=spf1 include:l{i + 1}.test -all");
        dns.AddTxt("l12.test", "v=spf1 +all");
        Assert.Equal(AuthCheckResult.PermError, (await Check(dns, "192.0.2.1")).Result);
    }

    [Fact]
    public async Task Include_loop_terminates_with_permerror()
    {
        var dns = new FakeDns().AddTxt("sender.test", "v=spf1 include:sender.test -all");
        Assert.Equal(AuthCheckResult.PermError, (await Check(dns, "192.0.2.1")).Result);
    }

    [Fact]
    public async Task Dns_failure_is_temperror()
    {
        var dns = new FakeDns();
        dns.Failing.Add("sender.test");
        Assert.Equal(AuthCheckResult.TempError, (await Check(dns, "192.0.2.1")).Result);
    }

    [Fact]
    public async Task Exists_with_ip_macro()
    {
        var dns = new FakeDns().AddTxt("sender.test", "v=spf1 exists:%{ir}.allow.sender.test -all").AddA("1.2.0.192.allow.sender.test", "127.0.0.2");
        Assert.Equal(AuthCheckResult.Pass, (await Check(dns, "192.0.2.1")).Result);
        Assert.Equal(AuthCheckResult.Fail, (await Check(dns, "192.0.2.2")).Result);
    }

    [Fact]
    public void Macro_expansion_follows_rfc_examples()
    {
        var ip = IPAddress.Parse("192.0.2.3");
        Assert.Equal("strong-bad@email.example.com", SpfEvaluator.Expand("%{s}", ip, "email.example.com", "strong-bad@email.example.com", "h"));
        Assert.Equal("example.com", SpfEvaluator.Expand("%{d2}", ip, "email.example.com", "s@x", "h"));
        Assert.Equal("com.example.email", SpfEvaluator.Expand("%{dr}", ip, "email.example.com", "s@x", "h"));
        Assert.Equal("3.2.0.192.in-addr._spf.example.com", SpfEvaluator.Expand("%{ir}.%{v}._spf.%{d2}".Replace("%{v}", "in-addr"), ip, "email.example.com", "s@x", "h"));
    }
}

public sealed class DmarcTests
{
    private static SpfOutcome Spf(AuthCheckResult r, string domain) => new(r, domain, "");
    private static DkimOutcome Dkim(params string[] passing) => new(passing.Length > 0 ? AuthCheckResult.Pass : AuthCheckResult.None, passing, "");

    [Fact]
    public async Task Aligned_spf_or_dkim_passes()
    {
        var dns = new FakeDns().AddTxt("_dmarc.sender.test", "v=DMARC1; p=reject");
        var e = new DmarcEvaluator(dns);
        Assert.Equal(AuthCheckResult.Pass, (await e.EvaluateAsync("sender.test", Spf(AuthCheckResult.Pass, "bounce.sender.test"), Dkim(), default)).Result);
        Assert.Equal(AuthCheckResult.Pass, (await e.EvaluateAsync("sender.test", Spf(AuthCheckResult.Fail, "other.test"), Dkim("sender.test"), default)).Result);
    }

    [Fact]
    public async Task Unaligned_pass_fails_and_reports_policy()
    {
        var dns = new FakeDns().AddTxt("_dmarc.sender.test", "v=DMARC1; p=reject");
        var result = await new DmarcEvaluator(dns).EvaluateAsync("sender.test", Spf(AuthCheckResult.Pass, "attacker.test"), Dkim("attacker.test"), default);
        Assert.Equal(AuthCheckResult.Fail, result.Result);
        Assert.Equal(DmarcPolicy.Reject, result.Policy);
    }

    [Fact]
    public async Task Strict_alignment_requires_exact_domain()
    {
        var dns = new FakeDns().AddTxt("_dmarc.sender.test", "v=DMARC1; p=quarantine; aspf=s");
        var result = await new DmarcEvaluator(dns).EvaluateAsync("sender.test", Spf(AuthCheckResult.Pass, "bounce.sender.test"), Dkim(), default);
        Assert.Equal(AuthCheckResult.Fail, result.Result);
    }

    [Fact]
    public async Task Subdomain_uses_organizational_record_and_sp()
    {
        var dns = new FakeDns().AddTxt("_dmarc.sender.test", "v=DMARC1; p=reject; sp=quarantine");
        var result = await new DmarcEvaluator(dns).EvaluateAsync("news.sender.test", Spf(AuthCheckResult.Fail, "x.test"), Dkim(), default);
        Assert.Equal(DmarcPolicy.Quarantine, result.Policy);
    }

    [Fact]
    public async Task No_record_is_none()
    {
        var result = await new DmarcEvaluator(new FakeDns()).EvaluateAsync("sender.test", Spf(AuthCheckResult.Fail, "x"), Dkim(), default);
        Assert.Equal(AuthCheckResult.None, result.Result);
    }

    [Theory]
    [InlineData("mail.examplecorp.net", "examplecorp.net")]
    [InlineData("a.b.example.co.uk", "example.co.uk")]
    [InlineData("shop.example.com.pk", "example.com.pk")]
    [InlineData("example.com", "example.com")]
    public void Organizational_domain(string domain, string expected) => Assert.Equal(expected, OrganizationalDomain.Of(domain));
}
