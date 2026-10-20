-- 592: did the release of domain ownership proof go as planned? Read-only: it changes nothing. Run it once the
-- release is live on both apps. One row per finding:
--   "release step not finished"            -- the start-up step that sets the live customers apart has not run to
--                                             its end: restart the web app and run this again
--   "added from 15 Oct, proven, no record"  -- must not exist: such a row needs its adviser's TXT record
--   "unproven, added before 15 Oct"         -- expected ONLY for the rows check 3 (domains-needing-proof.sql) listed
--                                             as "never connected"; any other is a live customer left unproven:
--                                             send it at once
--   "www.411trades.com still on an account" -- the owner's 15 Oct test, expected gone before the release
SELECT 'release step not finished' AS finding, NULL AS row_id, NULL AS DomainName, NULL AS UserName
FROM information_schema.COLUMNS c
WHERE c.TABLE_SCHEMA = DATABASE() AND LOWER(c.TABLE_NAME) = 'agentdomains' AND c.COLUMN_NAME = 'OwnershipVerifiedAt'
  AND c.COLUMN_DEFAULT IS NOT NULL
UNION ALL
SELECT 'added from 15 Oct, proven, no record', d.Id, d.DomainName, u.UserName
FROM AgentDomains d LEFT JOIN AgentUsers u ON u.Id = d.AgentUserId
WHERE d.CreatedAt >= '2026-10-15 00:00:00' AND d.OwnershipVerifiedAt IS NOT NULL AND d.VerificationToken = ''
UNION ALL
SELECT 'unproven, added before 15 Oct', d.Id, d.DomainName, u.UserName
FROM AgentDomains d LEFT JOIN AgentUsers u ON u.Id = d.AgentUserId
WHERE d.CreatedAt < '2026-10-15 00:00:00' AND d.OwnershipVerifiedAt IS NULL
UNION ALL
SELECT 'www.411trades.com still on an account', d.Id, d.DomainName, u.UserName
FROM AgentDomains d LEFT JOIN AgentUsers u ON u.Id = d.AgentUserId
WHERE LOWER(d.DomainName) IN ('www.411trades.com', '411trades.com') OR LOWER(d.RootDomain) = '411trades.com'
ORDER BY finding, row_id;
