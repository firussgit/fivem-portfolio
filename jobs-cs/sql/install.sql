-- Also executed automatically by the resource on start (CREATE TABLE IF NOT EXISTS).
-- Only the job name and grade index are stored. Labels and salaries come from the
-- server-side job definitions, so editing a salary never needs a database migration.
CREATE TABLE IF NOT EXISTS player_jobs (
    license    VARCHAR(64) PRIMARY KEY,
    job        VARCHAR(32) NOT NULL DEFAULT 'unemployed',
    grade      INT         NOT NULL DEFAULT 0,
    on_duty    TINYINT     NOT NULL DEFAULT 0,
    updated_at TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);

-- Append-only history of courier/taxi runs. Payouts are calculated on the server at the
-- moment a run is completed; cancelled, expired and abandoned runs are logged with 0.
CREATE TABLE IF NOT EXISTS job_runs (
    id         INT AUTO_INCREMENT PRIMARY KEY,
    license    VARCHAR(64) NOT NULL,
    job        VARCHAR(32) NOT NULL,
    activity   VARCHAR(32) NOT NULL,
    route      VARCHAR(64) NOT NULL,
    stops      INT         NOT NULL,
    payout     INT         NOT NULL,
    seconds    INT         NOT NULL,
    status     VARCHAR(16) NOT NULL,   -- completed, cancelled, expired, abandoned
    created_at TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_license (license)
);

-- Append-only history of on-duty field actions: police fines and impounds, EMS revives,
-- mechanic repairs. "amount" is the money moved (fine charged, revive/repair paid); it is 0
-- for an impound. target_* is the player affected, or empty for an unowned target/vehicle.
CREATE TABLE IF NOT EXISTS job_actions (
    id             INT AUTO_INCREMENT PRIMARY KEY,
    actor_license  VARCHAR(64) NOT NULL,
    actor_name     VARCHAR(64) NOT NULL,
    action         VARCHAR(16) NOT NULL,   -- fine, impound, revive, repair
    target_license VARCHAR(64) NULL,
    target_name    VARCHAR(64) NULL,
    amount         INT         NOT NULL DEFAULT 0,
    detail         VARCHAR(160) NOT NULL,
    created_at     TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_actor (actor_license),
    INDEX idx_action (action)
);
