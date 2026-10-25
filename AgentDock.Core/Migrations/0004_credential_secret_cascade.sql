ALTER TABLE dbo.ProviderCredential DROP CONSTRAINT FK_ProviderCredential_Secret;
GO
ALTER TABLE dbo.ProviderCredential ADD CONSTRAINT FK_ProviderCredential_Secret
    FOREIGN KEY (AccountId, SecretId) REFERENCES dbo.Secret(AccountId, Id) ON DELETE CASCADE;
GO
