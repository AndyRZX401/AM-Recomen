using System;
using AMRecomen.Application.Dtos;
using AMRecomen.Domain.Enums;

namespace AMRecomen.Application.Common;

public static class HypeCalculator
{
    public static double CalculateHypeScore(ExternalMediaResult ext)
    {
        if (ext == null)
            return 50.0;

        // Puntuación base de Hype entre 10% y 99% según el tipo de contenido y sus métricas de origen
        double hype = 50.0;

        switch (ext.Type)
        {
            case MediaType.Anime:
                // MAL members suele ir de 0 a 3,000,000+. Escalamos logarítmicamente.
                if (ext.PopularityScore > 0)
                {
                    hype = Math.Min(98.0, 20.0 + Math.Log10(ext.PopularityScore + 1) * 13.0);
                }
                break;

            case MediaType.Pelicula:
            case MediaType.Serie:
                // TMDb popularity suele ir de 10 a 2000+.
                if (ext.PopularityScore > 0)
                {
                    hype = Math.Min(98.0, 30.0 + (ext.PopularityScore / 15.0));
                }
                break;

            case MediaType.Manhwa:
            case MediaType.Manhua:
                // MangaDex followedCount va de 0 a 150,000+.
                if (ext.PopularityScore > 0)
                {
                    hype = Math.Min(98.0, 25.0 + Math.Log10(ext.PopularityScore + 1) * 15.0);
                }
                break;
        }

        // Ponderación del 20% basada en la valoración (Rating de 0 a 10)
        if (ext.Rating > 0)
        {
            hype = (hype * 0.8) + (ext.Rating * 2.0);
        }

        return Math.Clamp(Math.Round(hype, 1), 10.0, 99.0);
    }
}
