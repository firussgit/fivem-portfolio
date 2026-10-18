-- Also executed automatically by the resource on start (CREATE TABLE IF NOT EXISTS).
-- Append-only purchase history. Prices and stock limits live in code; current stock is in memory.
CREATE TABLE IF NOT EXISTS shop_purchases (
    id         INT AUTO_INCREMENT PRIMARY KEY,
    license    VARCHAR(64)  NOT NULL,
    shop       VARCHAR(32)  NOT NULL,
    item       VARCHAR(32)  NOT NULL,
    quantity   INT          NOT NULL,
    total      INT          NOT NULL,
    account    VARCHAR(8)   NOT NULL,   -- cash or bank
    created_at TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_license (license)
);
