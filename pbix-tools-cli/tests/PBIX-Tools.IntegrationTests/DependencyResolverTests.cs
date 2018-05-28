﻿using System.IO;
using PbixTools.Actions;
using PbixTools.Utils;
using Xunit;
using static PbixTools.Utils.Resources;

namespace PbixTools.IntegrationTests
{
    public class DependencyResolverTests : IClassFixture<DependencyResolverFixture>
    {
        private readonly DependencyResolverFixture _fixture;

        public DependencyResolverTests(DependencyResolverFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public void Can_get_msmdsrv_path()
        {
            if (_fixture.DependenciesResolver.TryFindMsmdsrv(out var path))
            {
                Assert.True(File.Exists(path));
                Assert.Equal("msmdsrv.exe", Path.GetFileName(path).ToLower());
            }
            else
            {
                Assert.True(false, "Could not find msmdsrv.exe");
            }
        }

        [Fact]
        public void Can_use_Mashup_Packaging_API()
        {
            using (var pbixSrc = GetEmbeddedResourceStream("Simple.pbix"))
            using (var tmp = new TempFolder())
            {
                var pbixPath = Path.Combine(tmp.Path, "Simple.pbix");
                using (var dest = File.Create(pbixPath))
                {
                    pbixSrc.CopyTo(dest);
                }

                using (var extractor = new PbixExtractAction(pbixPath, _fixture.DependenciesResolver))
                {
                    extractor.ExtractMashup(); // This one will require to load the Mashup.Packaging dll
                }

                // Double-check that the M script has actually be extracted
                Assert.True(File.Exists(Path.Combine(
                    Path.GetDirectoryName(pbixPath),
                    Path.GetFileNameWithoutExtension(pbixPath),
                    "Mashup",
                    "Package",
                    "Formulas",
                    "Section1.m")));
            }
        }

    }

    public class DependencyResolverFixture
    {
        public IDependenciesResolver DependenciesResolver { get; }

        public DependencyResolverFixture()
        {
            DependenciesResolver = new DependenciesResolver();
        }
    }
}
