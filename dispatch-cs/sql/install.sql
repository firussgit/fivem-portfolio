-- Also executed automatically by the resource on start (CREATE TABLE IF NOT EXISTS).
-- One row per call. Active calls live in memory too, and the server closes any non-closed row
-- when it starts, so "responding" can never survive a restart.
CREATE TABLE IF NOT EXISTS dispatch_calls (
    id             INT AUTO_INCREMENT PRIMARY KEY,
    caller_license VARCHAR(64) NOT NULL,
    caller_name    VARCHAR(64) NOT NULL,
    category       VARCHAR(16) NOT NULL,   -- police | medical
    message        VARCHAR(160) NOT NULL,
    x              DOUBLE NOT NULL,
    y              DOUBLE NOT NULL,
    z              DOUBLE NOT NULL,
    area           VARCHAR(64) NOT NULL,   -- nearest named district, chosen by the server
    status         VARCHAR(16) NOT NULL DEFAULT 'open',   -- open | claimed | enroute | onscene | closed
    claimed_by     VARCHAR(64) NULL,
    claimed_name   VARCHAR(64) NULL,
    claimed_at     DATETIME NULL,
    outcome        VARCHAR(24) NULL,       -- resolved | false_alarm | no_response | cancelled | expired
    payout         INT NOT NULL DEFAULT 0,
    created_at     TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    closed_at      DATETIME NULL,
    INDEX idx_status (status),
    INDEX idx_caller (caller_license),
    INDEX idx_claimed (claimed_by)
);
