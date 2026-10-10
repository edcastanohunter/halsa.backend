using System.Net;
using System.Net.Http.Json;
using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.WorkItems.Queries;
using Halsa.Backend.FunctionalTests.Abstractions;

namespace Halsa.Backend.FunctionalTests.WorkItems;

[Collection(nameof(ApiCollection))]
public sealed class WorkItemEndpointsTests(ApiFactory factory)
{
    private sealed record CreateResponse(Guid Id);

    [Fact]
    public async Task Create_ShouldReturnCreated_AndTheItemShouldBeReadable()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "work-items",
            new { title = "Functional", description = "end to end" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        CreateResponse? created = await response.Content.ReadFromJsonAsync<CreateResponse>();
        created.ShouldNotBeNull();

        WorkItemResponse? fetched = await client.GetFromJsonAsync<WorkItemResponse>($"work-items/{created.Id}");

        fetched.ShouldNotBeNull();
        fetched.Title.ShouldBe("Functional");
        fetched.Status.ShouldBe("Open");
    }

    [Fact]
    public async Task Create_ShouldReturnBadRequest_WhenTitleIsMissing()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "work-items",
            new { title = "", description = (string?)null });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetById_ShouldReturnNotFound_ForAnUnknownId()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync($"work-items/{Guid.CreateVersion7()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Complete_ShouldReturnConflict_WhenCalledTwice()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage created = await client.PostAsJsonAsync(
            "work-items",
            new { title = "Complete twice", description = (string?)null });

        CreateResponse? item = await created.Content.ReadFromJsonAsync<CreateResponse>();

        (await client.PostAsync($"work-items/{item!.Id}/complete", content: null))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.PostAsync($"work-items/{item.Id}/complete", content: null))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task List_ShouldReturnAPage()
    {
        HttpClient client = factory.CreateClient();

        await client.PostAsJsonAsync("work-items", new { title = "Listed", description = (string?)null });

        PagedList<WorkItemResponse>? page =
            await client.GetFromJsonAsync<PagedList<WorkItemResponse>>("work-items?page=1&pageSize=5");

        page.ShouldNotBeNull();
        page.Items.ShouldNotBeEmpty();
        page.PageSize.ShouldBe(5);
    }
}
