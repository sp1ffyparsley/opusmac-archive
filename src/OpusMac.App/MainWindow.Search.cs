using Avalonia.Controls;
using OpusMac.Core;
using OpusMac.Core.Search;
using OpusMac.Core.Tools;

namespace OpusMac.App;

public sealed partial class MainWindow
{
    private ListarySearchPage? _searchPage;
    private PaneView? _searchPane;
    private readonly Button _moreSearch = Ui.Button("Next", () => { }, "Next page of indexed results");
    private long _searchRequestGeneration;
    private Task _pendingSearch = Task.CompletedTask;
    private CancellationTokenSource? _searchToolCancellation;
    private CancellationTokenSource? _searchHealthCancellation;
    private (long Navigation, long Filter) _publishedSearchRevision;

    private Task SearchAsync()
    {
        var generation = ++_searchRequestGeneration;
        var query = _search.Text?.Trim() ?? "";
        var pane = ActivePane;
        var scope = _searchScope.SelectedIndex;
        var revision = pane.ViewRevision;
        StopCurrentSearch();
        var previous = _pendingSearch;
        _pendingSearch = SearchCoreAsync(generation, query, scope, pane, revision, previous);
        return _pendingSearch;
    }

    private void StopCurrentSearch()
    {
        if (_searchToolCancellation is not null && ReferenceEquals(_searchToolCancellation, _toolCancellation)) _searchToolCancellation.Cancel();
        _searchHealthCancellation?.Cancel();
    }

    private async Task ClearSearchAsync()
    {
        ++_searchRequestGeneration; StopCurrentSearch();
        _searchPage = null; _moreSearch.IsEnabled = false;
        _search.Text = "";
        var pane = ActivePane; pane.FilterText = ""; await pane.RefreshAsync();
    }

    private async Task SearchCoreAsync(long generation, string query, int scope, PaneView pane,
        (long Navigation, long Filter) revision, Task previous)
    {
        _searchPage = null; _moreSearch.IsEnabled = false;
        try { await previous; } catch (Exception) { }
        if (generation != _searchRequestGeneration || pane.ViewRevision != revision || _closingState) return;
        EnsurePaneReady(pane);
        if (query.Length == 0) { pane.FilterText = ""; await pane.RefreshAsync(); return; }
        if (scope == 0)
        {
            var directory = pane.CurrentDirectory;
            if (pane.IsSearchResults) await pane.RefreshAsync();
            if (generation != _searchRequestGeneration || pane.CurrentDirectory != directory) return;
            pane.FilterText = query; await pane.FilteringTask;
            if (pane == ActivePane) pane.FocusList();
            return;
        }
        try
        {
            if (scope == 1)
            {
                var directory = pane.CurrentDirectory;
                var listing = await RunToolAsync("Searching subfolders", (token, _) =>
                {
                    _searchToolCancellation = _toolCancellation;
                    return _browser.ReadAsync(directory, new BrowseOptions(ShowHidden: _settings.ShowHidden, Recursive: true, Filter: query),
                        new Progress<DirectoryBatch>(batch => { if (generation == _searchRequestGeneration) _jobText.Text = $"Searching subfolders · {batch.Discovered:N0} matches"; }), token);
                });
                if (generation != _searchRequestGeneration || pane.ViewRevision != revision || _closingState) return;
                pane.ShowResults(listing.Entries, $"Subfolders of {directory}");
                var resultRevision = pane.ViewRevision;
                await pane.LoadingTask;
                if (generation != _searchRequestGeneration || pane.ViewRevision != resultRevision) return;
                _message.Text = $"{listing.Entries.Count:N0} matches{(listing.Truncated ? " · Scan limit reached; narrow the location" : "")}{(listing.Errors.Count > 0 ? $" · {listing.Errors.Count} read error(s): {listing.Errors[0]}" : "")}";
            }
            else
            {
                var page = await RunToolAsync("Searching Listary index", (token, _) => { _searchToolCancellation = _toolCancellation; return _listary.SearchAsync(query, 200, token); });
                if (generation != _searchRequestGeneration || pane.ViewRevision != revision || _closingState) return;
                await ShowIndexedPageAsync(pane, page, generation);
            }
            if (generation == _searchRequestGeneration && pane == ActivePane) pane.FocusList();
        }
        catch (OperationCanceledException) when (generation != _searchRequestGeneration || _closingState) { }
        finally { _searchToolCancellation = null; }
    }

    private async Task ShowIndexedPageAsync(PaneView pane, ListarySearchPage page, long generation)
    {
        pane.ShowResults(page.Results.Select(result => result.Entry).ToArray(), $"Listary · {page.Request.Query}", page.Results);
        var revision = pane.ViewRevision;
        await pane.LoadingTask;
        if (generation != _searchRequestGeneration || pane.ViewRevision != revision || _closingState) return;
        _searchPane = pane; _searchPage = page; _publishedSearchRevision = revision;
        _moreSearch.IsEnabled = page.HasMore;
        _message.Text = $"Listary · {page.TotalMatches:N0} matches · rows {(page.Results.Count == 0 ? 0 : page.Offset + 1):N0}–{page.Offset + page.Results.Count:N0}"
            + (page.RejectedResults > 0 ? $" · {page.RejectedResults:N0} unavailable result(s) excluded" : "");
        if (page.TotalMatches == 0 && page.Offset == 0 && page.Results.Count == 0)
        {
            // An empty shared catalog is not necessarily a genuine no-match.
            // This secondary read must cancel when a newer query/selection
            // arrives, never blocking a subsequent search on stale status.
            using var health = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCancellation.Token);
            _searchHealthCancellation = health;
            try
            {
                var readiness = await _listary.GetIndexReadinessAsync(health.Token);
                if (generation != _searchRequestGeneration || pane.ViewRevision != revision || _closingState) return;
                if (!readiness.IsAvailable)
                    _message.Text = $"Listary · {readiness.Message}";
            }
            finally
            {
                if (ReferenceEquals(_searchHealthCancellation, health)) _searchHealthCancellation = null;
            }
        }
    }

    private async Task NextSearchPageAsync()
    {
        if (_searchPage?.NextRequest is not { } request || _searchPane is null) return;
        if (!_searchPane.IsSearchResults || _searchPane.ViewRevision != _publishedSearchRevision) { _moreSearch.IsEnabled = false; _message.Text = "Run the indexed search again to load more results"; return; }
        var pane = _searchPane;
        var revision = pane.ViewRevision;
        var generation = ++_searchRequestGeneration;
        StopCurrentSearch();
        var previous = _pendingSearch;
        _pendingSearch = NextSearchPageCoreAsync(previous, pane, request, generation, revision);
        await _pendingSearch;
    }

    private async Task NextSearchPageCoreAsync(Task previous, PaneView pane, ListarySearchRequest request, long generation,
        (long Navigation, long Filter) revision)
    {
        try { await previous; } catch (Exception) { }
        if (generation != _searchRequestGeneration || pane.ViewRevision != revision || _closingState) return;
        try
        {
            var page = await RunToolAsync("Loading Listary results", (token, _) => { _searchToolCancellation = _toolCancellation; return _listary.SearchPageAsync(request, token); });
            if (generation != _searchRequestGeneration || pane.ViewRevision != revision || _closingState) return;
            await ShowIndexedPageAsync(pane, page, generation);
        }
        catch (OperationCanceledException) when (generation != _searchRequestGeneration) { }
        finally { _searchToolCancellation = null; }
    }

    private async Task<T> RunToolAsync<T>(string description, Func<CancellationToken, IProgress<ToolProgress>, Task<T>> work)
    {
        EnsureAcceptingWork();
        if (_toolCancellation is not null) throw new InvalidOperationException("Another search or file tool is running. Cancel it or wait for it to finish.");
        var cancellation = _toolCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCancellation.Token);
        _jobText.Text = description; _jobProgress.IsIndeterminate = true; _cancel.IsEnabled = true;
        UpdateJobPanelVisibility();
        var progress = new Progress<ToolProgress>(value =>
        {
            if (_disposing || !ReferenceEquals(cancellation, _toolCancellation)) return;
            _jobText.Text = $"{value.Description} · {value.CompletedItems:N0}";
            _jobProgress.IsIndeterminate = value.TotalItems == 0;
            if (value.TotalItems > 0) _jobProgress.Value = 100d * value.CompletedItems / value.TotalItems;
        });
        try
        {
            var task = work(cancellation.Token, progress);
            _toolTask = task;
            return await task;
        }
        finally
        {
            _toolCancellation = null; cancellation.Dispose();
            _cancel.IsEnabled = _jobs.Count > 0;
            if (_jobs.Count == 0) { _jobProgress.IsIndeterminate = false; _jobProgress.Value = 0; _jobText.Text = "No file operations running"; }
            UpdateJobPanelVisibility();
        }
    }
}
