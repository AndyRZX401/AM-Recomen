using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AMRecomen.Application.Common;

public static class SlugHelper
{
    public static string GenerateSlug(string phrase, string suffix)
    {
        var str = phrase.ToLowerInvariant();
        
        // Remover acentos y caracteres diacríticos
        str = RemoveDiacritics(str);
        
        // Remover caracteres inválidos
        str = Regex.Replace(str, @"[^a-z0-9\s-]", "");
        
        // Convertir múltiples espacios o guiones en uno solo
        str = Regex.Replace(str, @"[\s-]+", " ").Trim();
        
        // Limitar caracteres
        str = str.Substring(0, Math.Min(str.Length, 150)).Trim();
        
        // Reemplazar espacios por guiones
        str = Regex.Replace(str, @"\s", "-");

        if (!string.IsNullOrWhiteSpace(suffix))
        {
            str = $"{str}-{suffix.ToLowerInvariant().Trim()}";
        }

        return str;
    }

    private static string RemoveDiacritics(string text)
    {
        var normalizedString = text.Normalize(NormalizationForm.FormD);
        var stringBuilder = new StringBuilder();

        foreach (var c in normalizedString)
        {
            var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != UnicodeCategory.NonSpacingMark)
            {
                stringBuilder.Append(c);
            }
        }

        return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
    }
}
