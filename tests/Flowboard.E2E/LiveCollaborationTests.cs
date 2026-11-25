using Microsoft.Playwright;

namespace Flowboard.E2E;

/// <summary>
/// Two separate browsers (two different guests) work on the same board and see each other's changes
/// live. Runs against a running app: FLOWBOARD_URL=http://localhost:5173 (CI: the Docker image).
/// With E2E_FRAMES_DIR set, screenshots of both browsers are saved after every step (used for the README GIF).
/// </summary>
public sealed class LiveCollaborationTests : IAsyncLifetime
{
    private static readonly string? BaseUrl = Environment.GetEnvironmentVariable("FLOWBOARD_URL")?.TrimEnd('/');
    private static readonly string? FramesDir = Environment.GetEnvironmentVariable("E2E_FRAMES_DIR");
    private static readonly float Timeout = 15_000;

    private IPlaywright? playwright;
    private IBrowser? browser;
    private int frame;

    public async Task InitializeAsync()
    {
        if (BaseUrl is null)
        {
            return;
        }

        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync();
    }

    public async Task DisposeAsync()
    {
        if (browser is not null)
        {
            await browser.DisposeAsync();
        }

        playwright?.Dispose();
    }

    [SkippableFact]
    public async Task Two_people_see_each_others_changes_without_reloading()
    {
        Skip.If(BaseUrl is null, "Set FLOWBOARD_URL to run the end-to-end tests against a running app.");

        // Two isolated browser contexts: separate cookies, so two different guests.
        var ana = await NewPageAsync();
        var ben = await NewPageAsync();

        // Ana starts the demo; Ben opens her invite link.
        await ana.GotoAsync($"{BaseUrl}/demo");
        await Expect(ana.Locator("h1")).ToHaveTextAsync("Website relaunch", new() { Timeout = Timeout });
        var invite = await ana.Locator("a[href^='join/']").GetAttributeAsync("href");
        await ben.GotoAsync($"{BaseUrl}/{invite}");
        await Expect(ben.Locator("h1")).ToHaveTextAsync("Website relaunch", new() { Timeout = Timeout });
        await Interactive(ana);
        await Interactive(ben);

        // Both see two people on the board.
        await Expect(ana.Locator(".presence .avatar")).ToHaveCountAsync(2, new() { Timeout = Timeout });
        await Expect(ben.Locator(".presence .avatar")).ToHaveCountAsync(2, new() { Timeout = Timeout });
        await SnapAsync(ana, ben);

        // Ana adds a card; it appears for Ben.
        var backlog = ana.Locator("[data-column='Backlog']");
        await backlog.GetByRole(AriaRole.Button, new() { Name = "+ Add a card" }).ClickAsync();
        await backlog.GetByPlaceholder("Card title").FillAsync("Ship the live demo");
        await backlog.GetByRole(AriaRole.Button, new() { Name = "Add card" }).ClickAsync();
        await Expect(ben.Locator("[data-column='Backlog'] [data-card='Ship the live demo']")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await SnapAsync(ana, ben);

        // Ben drags it to Done; it moves on Ana's screen.
        await ben.Locator("[data-card='Ship the live demo']").DragToAsync(ben.Locator("[data-column='Done'] .drop-end"));
        await Expect(ana.Locator("[data-column='Done'] [data-card='Ship the live demo']")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Expect(ana.Locator("[data-column='Backlog'] [data-card='Ship the live demo']")).ToHaveCountAsync(0);
        await Expect(ana.Locator(".toast")).ToContainTextAsync("moved “Ship the live demo” to Done", new() { Timeout = Timeout });
        await SnapAsync(ana, ben);

        // Ana opens the card: Ben sees that she is editing it.
        var anaName = (await ana.Locator(".topnav .muted").InnerTextAsync()).Trim();
        await ana.Locator("[data-card='Ship the live demo']").ClickAsync();
        await Expect(ben.Locator("[data-card='Ship the live demo'] .editing")).ToHaveTextAsync($"{anaName} is editing", new() { Timeout = Timeout });
        await SnapAsync(ana, ben);

        // Ben opens it too and saves first; Ana's save is rejected with an explanation.
        await ben.Locator("[data-card='Ship the live demo']").ClickAsync();
        await ben.Locator("#card-title").FillAsync("Ship the live demo today");
        await ben.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Expect(ben.Locator("[data-card='Ship the live demo today']")).ToBeVisibleAsync(new() { Timeout = Timeout });

        await Expect(ana.Locator(".dialog .notice.info")).ToContainTextAsync("Someone just changed this card", new() { Timeout = Timeout });
        await ana.Locator("#card-title").FillAsync("Ship it tomorrow");
        await ana.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Expect(ana.Locator(".dialog .notice.conflict")).ToContainTextAsync("changed this card while you were editing", new() { Timeout = Timeout });
        await SnapAsync(ana, ben);

        // The history shows who did what.
        await ana.Locator(".dialog").GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await ana.GetByRole(AriaRole.Button, new() { Name = "History" }).ClickAsync();
        await Expect(ana.Locator(".activity li").First).ToContainTextAsync("updated “Ship the live demo today”", new() { Timeout = Timeout });
        await SnapAsync(ana, ben);
    }

    private async Task<IPage> NewPageAsync()
    {
        var context = await browser!.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 760 } });
        var page = await context.NewPageAsync();
        // Browser errors end up in the test log, which makes CI failures readable.
        page.Console += (_, msg) => { if (msg.Type is "error" or "warning") { Console.WriteLine($"[browser {msg.Type}] {msg.Text}"); } };
        page.PageError += (_, error) => Console.WriteLine($"[page error] {error}");
        return page;
    }

    /// <summary>Waits until Blazor has connected the page, so clicks reach the server.</summary>
    private async Task Interactive(IPage page)
    {
        try
        {
            await page.WaitForFunctionAsync("() => window.Blazor !== undefined && document.querySelector('.presence .avatar') !== null",
                null, new() { Timeout = Timeout });
        }
        catch (TimeoutException)
        {
            if (!string.IsNullOrEmpty(FramesDir))
            {
                Directory.CreateDirectory(FramesDir);
                await page.ScreenshotAsync(new() { Path = Path.Combine(FramesDir, $"failure{++frame:D2}.png") });
            }

            throw;
        }
    }

    private async Task SnapAsync(IPage left, IPage right)
    {
        if (string.IsNullOrEmpty(FramesDir))
        {
            return;
        }

        Directory.CreateDirectory(FramesDir);
        frame++;
        await left.ScreenshotAsync(new() { Path = Path.Combine(FramesDir, $"step{frame:D2}_a.png") });
        await right.ScreenshotAsync(new() { Path = Path.Combine(FramesDir, $"step{frame:D2}_b.png") });
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
