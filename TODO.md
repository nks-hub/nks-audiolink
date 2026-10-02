# NKS AudioLink — postup práce

Stav se aktualizuje podle ověřených výsledků. Zaškrtnutí fáze vyžaduje i její skutečný test.

- [x] 0. Kostra řešení, lokální build/test, soukromý GitHub repozitář a zelené CI. Ověřeno 2026-10-02: CI Windows i Linux uspělo.
- [x] 1. Core: UDP protokol, HMAC, jitter buffer, korekce driftu a testy. Ověřeno 2026-10-02: 27 testů, pokrytí řádků Core 85,42 %; simulace driftu ±200 ppm po 10 minut.
- [x] 2. UDP server a sinky WAV/null; test tónu přes síť a kontrola 440 Hz. Ověřeno 2026-10-02 mezi Windows a Linux x86_64: WAV 5,095 s, RMS 8 484, dominantní 440 Hz, 0 přetečení.
- [ ] 3. ALSA sink, systemd nasazení a desetiminutový test na cílovém Linuxu. Služba nainstalovaná a aktivní; nejnovější 600s test přes systemd: 0 podtečení/přetečení/ztrát/pozdních rámců v klientovi i závěrečném záznamu serveru. Zbývá potvrzení poslechem a snížení zátěže z přibližně 3 % pod plánovaná 2 % jednoho jádra.
- [ ] 4. Windows WASAPI klient, tichý tok a ověření skutečného zvuku. Zachycený tón do WAV a živý výstup PC přes UI do vzdáleného ALSA ověřeny; čeká se na potvrzení poslechu. Test souvislého tichého toku prošel.
- [ ] 5. Změřit a zapsat latenci; cílově pod 100 ms. UI počítá se skutečnou periodou capture; při živém přenosu odhad 74 ms. Fyzické měření a kontrola synchronizace videa zbývají.
- [x] 6. WPF UI, tray, nastavení, discovery a snímky obrazovky. Ověřeno 2026-10-02: připojení/odpojení UI proti WAV serveru, živé statistiky, změna gain během přenosu, discovery proti skutečné službě a screenshot v docs/screenshots.
- [x] 7. Režim virtuální zvukovky a bezpečná obnova výchozího výstupu. Ověřeno 2026-10-02: samostatná instalace podepsaného VB-CABLE po souhlasu majitele bez restartu PC, skutečný 440Hz přenos CABLE Input → CABLE Output → UDP WAV a obnova všech tří výchozích rolí Windows po odpojení i dalším spuštění po simulovaném pádu. Ovladač není součástí projektu.
- [ ] 8. Obnova po výpadku sítě, restartu serveru a uspání; test takeover. Skutečný WASAPI klient obnovil přenos na LAN po restartu služby serveru. Šest Windows integračních testů pokrývá tichý tok, pozdní start serveru, restart serveru, výměnu capture po chybě zařízení a úplné zastavení odmítnutého klienta bez automatického opakování. Serverový takeover a opětovné otevření exkluzivního sinku pokrývají integrační testy. Fyzické uspání/probuzení PC zbývá; během poslechu se neprovádí.
- [ ] 9. Release workflow, kompletní README, changelog a ověřené artefakty. Lokální Windows a Linux balíčky sestaveny a struktura ověřena, publikované Windows CLI i Linux server spustily help. Workflow má ruční sestavení artefaktů bez vydání; tag v0.1.0 zbývá po dokončení hardwarových kontrol.

## Pravidla

- Nevkládat do Git historie údaje z interního inventáře, přístupové údaje, lokální konfiguraci ani binárky cizího ovladače.
- Změny verzovat po smysluplných celcích jako Conventional Commits.
- Přímé ovládání receiveru přes jeho vyhrazené řídicí spojení nepoužívat.
