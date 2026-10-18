-- Also executed automatically by the resource on start (CREATE TABLE IF NOT EXISTS).
CREATE TABLE IF NOT EXISTS player_needs (
    license    VARCHAR(64) PRIMARY KEY,
    hunger     FLOAT       NOT NULL DEFAULT 80,   -- 0 (starving) to 100 (full)
    thirst     FLOAT       NOT NULL DEFAULT 80,
    updated_at TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
