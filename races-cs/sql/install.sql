-- Also executed automatically by the resource on start (CREATE TABLE IF NOT EXISTS).
-- Append-only history of every finished time trial. Personal bests and the top-10 board are
-- derived with MIN(milliseconds) per license, so every attempt is kept.
CREATE TABLE IF NOT EXISTS race_times (
    id           INT AUTO_INCREMENT PRIMARY KEY,
    license      VARCHAR(64) NOT NULL,
    name         VARCHAR(64) NOT NULL,
    route        VARCHAR(32) NOT NULL,
    milliseconds INT         NOT NULL,
    model        VARCHAR(32) NOT NULL,
    created_at   TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_route (route, milliseconds),
    INDEX idx_license (license)
);
