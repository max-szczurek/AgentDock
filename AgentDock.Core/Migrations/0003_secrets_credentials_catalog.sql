CREATE TABLE dbo.Secret (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_Secret PRIMARY KEY,
    AccountId uniqueidentifier NOT NULL,
    Purpose nvarchar(40) NOT NULL,
    Algorithm nvarchar(20) NOT NULL CONSTRAINT DF_Secret_Algorithm DEFAULT 'A256GCM',
    Ciphertext varbinary(max) NOT NULL,
    Nonce binary(12) NOT NULL,
    Tag binary(16) NOT NULL,
    WrappedDek varbinary(512) NOT NULL,
    KekId nvarchar(400) NOT NULL,
    Hint nvarchar(32) NULL,
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Secret_Created DEFAULT SYSUTCDATETIME(),
    ShreddedUtc datetime2(3) NULL,
    CONSTRAINT UQ_Secret_AccountId_Id UNIQUE (AccountId, Id),
    CONSTRAINT CK_Secret_Purpose CHECK (Purpose IN ('AnthropicApiKey','ConnectionBearer','ConnectionHeader','EmbedSigningKey')),
    CONSTRAINT FK_Secret_Account FOREIGN KEY (AccountId) REFERENCES dbo.Account(Id) ON DELETE CASCADE
);
GO
CREATE TABLE dbo.ProviderCredential (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_ProviderCredential PRIMARY KEY,
    AccountId uniqueidentifier NOT NULL,
    Provider nvarchar(20) NOT NULL,
    SecretId uniqueidentifier NOT NULL,
    Status nvarchar(20) NOT NULL CONSTRAINT DF_ProviderCredential_Status DEFAULT 'Unverified',
    LastVerifiedUtc datetime2(3) NULL,
    LastErrorCode nvarchar(60) NULL,
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_ProviderCredential_Created DEFAULT SYSUTCDATETIME(),
    ReplacedUtc datetime2(3) NULL,
    CONSTRAINT UQ_ProviderCredential_AccountId_Id UNIQUE (AccountId, Id),
    CONSTRAINT CK_ProviderCredential_Status CHECK (Status IN ('Unverified','Valid','Invalid')),
    CONSTRAINT FK_ProviderCredential_Secret FOREIGN KEY (AccountId, SecretId) REFERENCES dbo.Secret(AccountId, Id)
);
GO
CREATE UNIQUE INDEX UX_ProviderCredential_Active ON dbo.ProviderCredential(AccountId, Provider) WHERE ReplacedUtc IS NULL;
GO
CREATE TABLE dbo.ModelCatalogSnapshot (
    ProviderCredentialId uniqueidentifier NOT NULL CONSTRAINT PK_ModelCatalogSnapshot PRIMARY KEY,
    FetchedUtc datetime2(3) NOT NULL,
    ModelsJson nvarchar(max) NOT NULL,
    ResolvedJson nvarchar(max) NOT NULL,
    CONSTRAINT CK_ModelCatalogSnapshot_Models CHECK (ISJSON(ModelsJson) = 1),
    CONSTRAINT CK_ModelCatalogSnapshot_Resolved CHECK (ISJSON(ResolvedJson) = 1),
    CONSTRAINT FK_ModelCatalogSnapshot_Credential FOREIGN KEY (ProviderCredentialId) REFERENCES dbo.ProviderCredential(Id) ON DELETE CASCADE
);
GO
