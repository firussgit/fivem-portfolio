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

-- One row per accepted insurance claim. The vehicle is repaired by the claim; "amount" is the
-- server-calculated settlement and "paid" records whether bank-cs accepted the deposit.
CREATE TABLE IF NOT EXISTS insurance_claims (
    id         INT AUTO_INCREMENT PRIMARY KEY,
    license    VARCHAR(64) NOT NULL,
    plate      VARCHAR(8)  NOT NULL,
    model      VARCHAR(32) NOT NULL,
    amount     INT         NOT NULL,
    paid       TINYINT     NOT NULL DEFAULT 0,
    created_at TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_plate (plate),
    INDEX idx_license (license)
);

-- Keys are granted per owned vehicle row, not per plate, and the unique key makes a repeated
-- grant idempotent. owner is denormalized for the owner-only grant/revoke statements; the
-- foreign key means selling (deleting) a vehicle takes its keys with it. grantee_name and
-- owner_name are display snapshots, so /keys never has to expose a licence identifier.
CREATE TABLE IF NOT EXISTS vehicle_keys (
    id           INT AUTO_INCREMENT PRIMARY KEY,
    vehicle_id   INT         NOT NULL,
    owner        VARCHAR(64) NOT NULL,
    grantee      VARCHAR(64) NOT NULL,
    owner_name   VARCHAR(64) NULL,
    grantee_name VARCHAR(64) NULL,
    created_at   TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY uq_vehicle_grantee (vehicle_id, grantee),
    INDEX idx_grantee (grantee),
    INDEX idx_owner (owner),
    CONSTRAINT fk_keys_vehicle FOREIGN KEY (vehicle_id) REFERENCES player_vehicles(id) ON DELETE CASCADE
);
