# Changelog

## Unreleased

- C#/.NET 9 server pro Linux s přímým ALSA výstupem, systemd službou a instalátorem.
- UDP PCM protokol s volitelným HMAC, jitter bufferem a korekcí rozdílných hodin zdroje a výstupu.
- Windows WASAPI klient pro loopback i záznamové zařízení; tichý tok zachovává časovou osu.
- Automatická obnova po výpadku serveru a chybě capture; odmítnutý klient se zastaví a čeká na ruční připojení.
- WPF aplikace s nastavením, discovery přes aktivní IPv4 rozhraní, tray a živými statistikami.
- Režim již nainstalované virtuální zvukovky VB-CABLE a obnova původních výstupů Windows po odpojení nebo následujícím startu po pádu.
- CLI pro seznam zařízení, discovery, přenos zvuku a testovací tón.
- Workflow pro sestavení Windows x64 aplikace a Linux x64 serveru a publikaci artefaktů při tagu vydání.

První vydání zůstává připravované, dokud neprojdou zbývající hardwarové a provozní kontroly uvedené v [TODO.md](TODO.md).
