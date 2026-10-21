using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AMRecomen.Application.Services;

public class TranslationService
{
    private readonly HttpClient _httpClient;

    public TranslationService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(4);
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        }
    }

    public async Task<string> TranslateToSpanishAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var textToTranslate = text.Length > 900 ? text.Substring(0, 900) : text;

        // 1. Motor rápido principal (Google GTX JSON endpoint - <100ms, sin avisos de cuota de MyMemory)
        try
        {
            var gtxUrl = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=es&dt=t&q={Uri.EscapeDataString(textToTranslate)}";
            var gtxResp = await _httpClient.GetAsync(gtxUrl);
            if (gtxResp.IsSuccessStatusCode)
            {
                var json = await gtxResp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var sentences = doc.RootElement[0];
                    if (sentences.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (var seg in sentences.EnumerateArray())
                        {
                            if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
                            {
                                sb.Append(seg[0].GetString());
                            }
                        }
                        var result = sb.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(result))
                        {
                            if (text.Length > 900)
                                result += "...";
                            return result;
                        }
                    }
                }
            }
        }
        catch
        {
            // Continuar al respaldo MyMemory
        }

        // 2. Respaldo secundario (MyMemory)
        try
        {
            var shortText = textToTranslate.Length > 450 ? textToTranslate.Substring(0, 450) : textToTranslate;
            var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(shortText)}&langpair=en|es";
            var response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var jsonString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(jsonString);
                if (doc.RootElement.TryGetProperty("responseData", out var respData) &&
                    respData.TryGetProperty("translatedText", out var transText))
                {
                    var translated = transText.GetString();
                    if (!string.IsNullOrWhiteSpace(translated) &&
                        !translated.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
                    {
                        return translated;
                    }
                }
            }
        }
        catch
        {
            // Fallback silencioso al texto original
        }

        return text;
    }
}
