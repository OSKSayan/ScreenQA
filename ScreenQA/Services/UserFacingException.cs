using System;

namespace ScreenQA.Services;

/// <summary>Ошибка с понятным пользователю текстом. <see cref="OfferSettings"/> — показать кнопку «Открыть настройки».</summary>
internal sealed class UserFacingException : Exception
{
    public UserFacingException(string message, bool offerSettings = false, Exception? inner = null)
        : base(message, inner)
    {
        OfferSettings = offerSettings;
    }

    public bool OfferSettings { get; }
}
