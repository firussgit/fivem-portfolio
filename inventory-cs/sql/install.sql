-- Also executed automatically by the resource on start (CREATE TABLE IF NOT EXISTS).
-- One row per occupied slot. Item definitions (label, weight, stack size) live in code.
CREATE TABLE IF NOT EXISTS player_inventory (
    license VARCHAR(64) NOT NULL,
    slot    INT         NOT NULL,
    item    VARCHAR(32) NOT NULL,
    amount  INT         NOT NULL,
    PRIMARY KEY (license, slot)
);
