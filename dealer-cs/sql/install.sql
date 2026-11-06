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
