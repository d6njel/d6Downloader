# Rapidgator API v2 (Auszug der offiziellen Doku, vom User am 2026-11-25 eingefügt)

Maßgeblich gegenüber früheren Annahmen.

## Allgemein
- Basis: `http(s)://rapidgator.net/api/v2/` (immer HTTPS verwenden). Alle Aufrufe GET oder POST, Antwort JSON.
- Fehlgeschlagene Anfrage nach spätestens 3 Versuchen aufgeben; keine unnötigen Aufrufe.
- Zu viele falsche Passwörter führen zur IP-Sperre.
- Erfolg: `{"response":{...},"status":200,"details":null}`
- Fehler: `{"response":null,"status":401,"details":"Error: Login or password is wrong"}`. Empfehlung der Doku: `details` dem User anzeigen.

## user/login (GET|POST)
Parameter: `login` (E-Mail), `password`, `code` (2FA, nur wenn aktiv).
`response.token` (String), `response.user`: `email`, `is_premium` (bool), `premium_end_time` (null bei Free), `state`, `state_label`,
`traffic.total/left` (null bei Free), `storage.total` (String!) / `storage.left` (Zahl), `upload.*`, `remote_upload.*`.
Fehler: 401 "Error: Login or password is wrong"; 401 "Error: Invalid auth code".

## user/info (GET|POST)
Parameter: `token`. Antwort `response.user` wie oben. Fehlerbeispiel: 404 "Error: User not found".

## file/info (GET|POST)
Parameter: `token`, `file_id`.
`response.file`: `file_id`, `mode`, `mode_label` (z. B. "Premium only"), `folder_id`, `name`, `hash` (32 Hex, sieht nach MD5 aus, nicht dokumentiert), `size`, `created`, `url` (`http://rapidgator.net/file/<32 Hex>/<name>`).
Fehler: 404 "Error: File not found".

## file/download (GET|POST)
Parameter: `token`, `file_id`.
`response.download_url` (im Beispiel http://pr3.rapidgator.net//?r=download/index&session_id=...), `response.delay` (Zahl, Sekunden vermutlich).
Fehler: 404 "Error: File not found".

## NICHT dokumentiert (nicht erfinden, tolerant behandeln)
Token-Lebensdauer und Fehler bei abgelaufenem Token; Antwort bei fehlendem 2FA-Code; Fehler bei erschöpftem Traffic, Captcha, gesperrtem Konto;
Lebensdauer der Download-URL; Range/Verbindungs-/IP-Limits; Bedeutung von "NO ACCESS" bei check_link; weitere Domains (rg.to).

## file/check_link (GET|POST)
Parameter: `token`, `url` (ein Link oder bis zu 25 kommagetrennt).
`response` ist ein **Array**: `url`, `status` ("ACCESS" | "NO ACCESS"), `filename` und `size` nur bei "ACCESS" (Feld heißt `filename`, nicht `name`).
Fehler: 400 "Error: Many links".
