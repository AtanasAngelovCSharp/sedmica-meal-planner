using Microsoft.JSInterop;

namespace MealApp.Services;

public class LocalStorageService
{
    private readonly IJSRuntime _js;
    public LocalStorageService(IJSRuntime js) => _js = js;

    public ValueTask<string?> GetAsync(string key) => _js.InvokeAsync<string?>("mealAppStorage.get", key);
    public ValueTask SetAsync(string key, string value) => _js.InvokeVoidAsync("mealAppStorage.set", key, value);
    public ValueTask RemoveAsync(string key) => _js.InvokeVoidAsync("mealAppStorage.remove", key);
}
