# Homebrew formula for cifail.
#
# Intended for a tap (e.g. `brew install SebHenn/tap/cifail`). The version and
# sha256 values below are filled in when a release is cut — see scripts/publish.sh
# output (`*.sha256`). Until the first tagged release exists these are placeholders.
class Cifail < Formula
  desc "Local-first CI failure intelligence: what broke, if it happened before, how to fix it"
  homepage "https://github.com/SebHenn/ci-failure-intelligence"
  version "0.1.0"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/SebHenn/ci-failure-intelligence/releases/download/v#{version}/cifail-#{version}-osx-arm64.tar.gz"
      sha256 "REPLACE_WITH_OSX_ARM64_SHA256"
    end
    on_intel do
      url "https://github.com/SebHenn/ci-failure-intelligence/releases/download/v#{version}/cifail-#{version}-osx-x64.tar.gz"
      sha256 "REPLACE_WITH_OSX_X64_SHA256"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/SebHenn/ci-failure-intelligence/releases/download/v#{version}/cifail-#{version}-linux-arm64.tar.gz"
      sha256 "REPLACE_WITH_LINUX_ARM64_SHA256"
    end
    on_intel do
      url "https://github.com/SebHenn/ci-failure-intelligence/releases/download/v#{version}/cifail-#{version}-linux-x64.tar.gz"
      sha256 "REPLACE_WITH_LINUX_X64_SHA256"
    end
  end

  def install
    bin.install "cifail"
  end

  test do
    assert_match "cifail", shell_output("#{bin}/cifail --help")
  end
end
