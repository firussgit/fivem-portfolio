-- Also executed automatically by the resource on start (CREATE TABLE IF NOT EXISTS).
-- One row per completed purchase or trade-in. The money movement itself lives in bank-cs;
-- this is the dealership's audit and sales history.
CREATE TABLE IF NOT EXISTS dealer_sales (
    id         INT AUTO_INCREMENT PRIMARY KEY,
    license    VARCHAR(64) NOT NULL,              -- Rockstar license of the buyer / seller
    kind       VARCHAR(8)  NOT NULL,              -- 'buy' or 'sell'
    model      VARCHAR(32) NOT NULL,
    plate      VARCHAR(8)  NOT NULL,
    amount     INT         NOT NULL,              -- price paid (buy) or paid out (sell)
    account    VARCHAR(8)  NOT NULL,              -- 'cash' or 'bank' (sales always pay to bank)
    created_at TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_license (license),
    INDEX idx_kind (kind)
);

-- One row per lease, including closed ones. A rental is NOT a player_vehicles row: it can't be
-- parked, sold or insured. `expires_ms` is the server's wall clock, so time offline still counts
-- and a relog can't reset the lease. ended_ms IS NULL means the lease is still open.
CREATE TABLE IF NOT EXISTS rentals (
    id          INT AUTO_INCREMENT PRIMARY KEY,
    license     VARCHAR(64) NOT NULL,
    model       VARCHAR(32) NOT NULL,
    plate       VARCHAR(8)  NOT NULL,              -- RENT####, server-issued
    account     VARCHAR(8)  NOT NULL,              -- 'cash' or 'bank'
    fee         INT         NOT NULL,              -- non-refundable
    deposit     INT         NOT NULL,              -- refunded only on a timely return
    started_ms  BIGINT      NOT NULL,
    expires_ms  BIGINT      NOT NULL,
    ended_ms    BIGINT      NULL,
    end_reason  VARCHAR(24) NULL,                  -- returned / expired / lost / failed
    active_license VARCHAR(64) GENERATED ALWAYS AS (IF(ended_ms IS NULL, license, NULL)) STORED,
    INDEX idx_license (license),
    INDEX idx_open (license, ended_ms),
    UNIQUE KEY uq_active_license (active_license)  -- at most one open lease per player
);
