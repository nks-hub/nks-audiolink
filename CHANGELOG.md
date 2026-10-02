# Historie změn

## Připravované vydání

- C#/.NET 9 server pro Linux s přímým výstupem do ALSA, službou systemd a instalátorem.
- Protokol UDP PCM s volitelným podpisem HMAC, vyrovnávací frontou a korekcí rozdílných hodin zvukových zařízení.
- Windows klient WASAPI pro zachytávání celého výstupu i záznamového zařízení; tiché rámce zachovávají časovou osu.
- Obnova po výpadku serveru a chybě zachytávání; po odmítnutí se klient zastaví a čeká na ruční připojení.
- WPF aplikace s nastavením, vyhledáním serveru přes aktivní rozhraní IPv4, ikonou v oznamovací oblasti a živými statistikami.
- Režim již nainstalované virtuální zvukovky VB-CABLE a obnova původních výstupů Windows po odpojení nebo následujícím startu po pádu.
- Příkazový klient pro seznam zařízení, vyhledání serveru, přenos zvuku a testovací tón.
- Automatické sestavení balíčků pro Windows x64 a Linux x64; označená verze spustí jejich vydání.
- Přibalený .NET runtime 9.0.20, pevně určené SDK 9.0.318 a licence runtime v balíčcích.
- Nastavení vláken a síťových dokončení na Linuxu snížilo zátěž procesoru při přenosu do fyzické ALSA trvajícím 600 s na 1,277 % jednoho jádra. Při cílové frontě 10 ms přibyly chyby toku; dlouhý test s 30 ms zbývá.

První vydání zůstává připravované, dokud neprojdou zbývající hardwarové a provozní kontroly uvedené v [TODO.md](TODO.md).
