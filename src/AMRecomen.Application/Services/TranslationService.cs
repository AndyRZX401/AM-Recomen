using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace AMRecomen.Application.Services;

public class TranslationService
{
    private readonly HttpClient _httpClient;

    public TranslationService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        // Timeout corto para evitar colgar la ingesta de datos
        _httpClient.Timeout = TimeSpan.FromSeconds(4);
    }

    public async Task<string> TranslateToSpanishAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        try
        {
            // Truncar para cumplir con límites de uso de la API MyMemory (máx. 1000 caracteres por llamada gratuita)
            var textToTranslate = text.Length > 850 ? text.Substring(0, 850) : text;
            
            var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(textToTranslate)}&langpair=en|es";
            
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return text; // Si falla, devolvemos el texto original en inglés

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (doc.RootElement.TryGetProperty("responseData", out var respData) &&
                respData.TryGetProperty("translatedText", out var transText))
            {
                var translated = transText.GetString();
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    if (text.Length > 850)
                        translated += "...";
                        
                    return translated;
                }
            }
        }
        catch
        {
            // Fallback silencioso al texto original en inglés
        }

        return text;
    }
}
