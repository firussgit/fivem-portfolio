-- Also executed automatically by the resource on start (CREATE TABLE IF NOT EXISTS).
CREATE TABLE IF NOT EXISTS player_vehicles (
    id         INT AUTO_INCREMENT PRIMARY KEY,
    owner      VARCHAR(64) NOT NULL,              -- Rockstar license of the owner
    model      VARCHAR(32) NOT NULL,
    plate      VARCHAR(8)  NOT NULL UNIQUE,
    state      TINYINT     NOT NULL DEFAULT 1,    -- 1 = stored in garage, 0 = out in the world
    stolen     TINYINT     NOT NULL DEFAULT 0,    -- 1 = hotwired; the police must recover it
    engine     INT         NOT NULL DEFAULT 1000, -- engine health, 0-1000
    body       INT         NOT NULL DEFAULT 1000, -- body health, 0-1000
    created_at TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_owner (owner)
);
