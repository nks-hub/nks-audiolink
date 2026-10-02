# NKS AudioLink — postup práce

Stav se aktualizuje podle ověřených výsledků. Zaškrtnutí fáze vyžaduje i její skutečný test.

- [ ] 0. Kostra řešení, lokální build/test, soukromý GitHub repozitář a zelené CI.
- [ ] 1. Core: přesný UDP protokol, HMAC, jitter buffer, korekce driftu a testy.
- [ ] 2. UDP server a sinky WAV/null; test tónu přes síť a kontrola 440 Hz.
- [ ] 3. ALSA sink, systemd nasazení a desetiminutový test na cílovém Linuxu.
- [ ] 4. Windows WASAPI klient, tichý tok a ověření skutečného zvuku.
- [ ] 5. Změřit a zapsat latenci; cílově pod 100 ms.
- [ ] 6. WPF UI, tray, nastavení, discovery a snímky obrazovky.
- [ ] 7. Režim virtuální zvukovky a bezpečná obnova výchozího výstupu. Instalace ovladače čeká na výslovné rozhodnutí majitele.
- [ ] 8. Obnova po výpadku sítě, restartu serveru a uspání; test takeover.
- [ ] 9. Release workflow, kompletní README, changelog a ověřené artefakty.

## Pravidla

- Nevkládat do Git historie údaje z interního inventáře, přístupové údaje, lokální konfiguraci ani binárky cizího ovladače.
- Změny verzovat po smysluplných celcích jako Conventional Commits.
- Přímé ovládání receiveru přes jeho vyhrazené řídicí spojení nepoužívat.
