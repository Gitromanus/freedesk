using System;
using System.Threading;

namespace FreeDesk.Agent;

/// <summary>
/// Подбирает свободный профиль для GUI, чтобы два запущенных окна не делили
/// один device token (и один ID). Мьютексы живут до конца процесса.
/// </summary>
public static class ProfilePicker
{
    private static readonly System.Collections.Generic.List<Mutex> Held = new();

    public static string Pick()
    {
        for (var n = 1; n < 100; n++)
        {
            var name = n == 1 ? "agent" : $"agent{n}";

            try
            {
                var mutex = new Mutex(true, $"Local\\FreeDesk_{name}", out var created);

                if (created)
                {
                    Held.Add(mutex);
                    return name;
                }

                mutex.Dispose();
            }
            catch
            {
                // ignore and try next
            }
        }

        return "agent" + Guid.NewGuid().ToString("N")[..6];
    }
}
