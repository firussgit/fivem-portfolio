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
